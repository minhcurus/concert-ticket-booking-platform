using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ConcertBooking.Api.Services;

public class BookingService : IBookingService
{
    private readonly AppDbContext _db;
    private readonly IIdempotencyCache _idempotencyCache;

    public BookingService(AppDbContext db, IIdempotencyCache idempotencyCache)
    {
        _db = db;
        _idempotencyCache = idempotencyCache;
    }

    public async Task<BookingResult> CreateBookingAsync(Guid userId, CreateBookingRequest request, string idempotencyKey, CancellationToken ct)
    {
        ValidateRequest(request);

        var cachedBookingId = await _idempotencyCache.TryGetAsync(idempotencyKey, ct);
        if (cachedBookingId is not null)
        {
            var cached = await GetBookingByIdAsync(userId, cachedBookingId.Value, ct);
            if (cached is not null) return new BookingResult(cached, true);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var categoryIds = request.Items.Select(i => i.TicketCategoryId).Distinct().OrderBy(id => id).ToList();
        var itemsByCategory = request.Items.ToDictionary(i => i.TicketCategoryId, i => i.Quantity);
        var categories = new List<TicketCategory>();

        foreach (var categoryId in categoryIds)
        {
            var category = await _db.TicketCategories
                .FromSqlInterpolated($"SELECT * FROM \"TicketCategories\" WHERE \"Id\" = {categoryId} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
            if (category is null) throw new TicketCategoryNotFoundException(categoryId);
            categories.Add(category);
        }

        foreach (var category in categories)
        {
            var requested = itemsByCategory[category.Id];
            if (category.AvailableQuantity < requested) throw new SoldOutException(category.Id);
            category.AvailableQuantity -= requested;
        }

        var totalAmount = categories.Sum(c => c.Price * itemsByCategory[c.Id]);
        Guid? voucherId = null;
        VoucherRedemption? redemption = null;

        if (!string.IsNullOrWhiteSpace(request.VoucherCode))
        {
            var voucher = await _db.Vouchers
                .FromSqlInterpolated($"SELECT * FROM \"Vouchers\" WHERE \"Code\" = {request.VoucherCode} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
            if (voucher is null) throw new InvalidVoucherException("Voucher not found");

            var now = DateTimeOffset.UtcNow;
            if (now < voucher.ValidFrom || now > voucher.ValidTo)
                throw new InvalidVoucherException("Voucher not currently valid");
            if (voucher.RedeemedCount >= voucher.MaxRedemptions)
                throw new InvalidVoucherException("Voucher fully redeemed");
            if (voucher.ConcertId is not null && voucher.ConcertId != categories[0].ConcertId)
                throw new InvalidVoucherException("Voucher not valid for this concert");

            var alreadyRedeemed = await _db.VoucherRedemptions
                .AnyAsync(r => r.VoucherId == voucher.Id && r.UserId == userId, ct);
            if (alreadyRedeemed) throw new InvalidVoucherException("Voucher already redeemed by this user");

            totalAmount = VoucherDiscountCalculator.Apply(totalAmount, voucher.DiscountType, voucher.DiscountValue);
            voucher.RedeemedCount += 1;
            voucherId = voucher.Id;

            redemption = new VoucherRedemption { Id = Guid.NewGuid(), VoucherId = voucher.Id, UserId = userId };
            _db.VoucherRedemptions.Add(redemption);
        }

        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ConcertId = categories[0].ConcertId,
            Status = BookingStatus.PendingPayment,
            IdempotencyKey = idempotencyKey,
            HoldExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            TotalAmount = totalAmount,
            VoucherId = voucherId,
        };

        foreach (var category in categories)
        {
            booking.Items.Add(new BookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                TicketCategoryId = category.Id,
                Quantity = itemsByCategory[category.Id],
                UnitPrice = category.Price,
            });
        }

        if (redemption is not null) redemption.BookingId = booking.Id;
        _db.Bookings.Add(booking);

        try
        {
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsIdempotencyKeyConflict(ex))
        {
            await transaction.RollbackAsync(ct);
            // Scope the replay lookup to this user, mirroring the Redis fast-path above
            // (GetBookingByIdAsync(userId, ...)). The unique index on IdempotencyKey is
            // global, so a key collision could belong to a different user entirely — in
            // that case this is a genuine conflict, not a safe idempotent replay, and must
            // not leak the other user's booking.
            var existing = await _db.Bookings.AsNoTracking()
                .Include(b => b.Items)
                .FirstOrDefaultAsync(b => b.IdempotencyKey == idempotencyKey && b.UserId == userId, ct);
            if (existing is null)
                throw new InvalidBookingStateException("Idempotency key already used by a different request");
            return new BookingResult(BookingMapper.ToDto(existing), true);
        }

        await _idempotencyCache.SetAsync(idempotencyKey, booking.Id, TimeSpan.FromMinutes(15), ct);

        var dto = await GetBookingByIdAsync(userId, booking.Id, ct)
            ?? throw new InvalidOperationException("Booking vanished immediately after commit");
        return new BookingResult(dto, false);
    }

    public async Task<BookingDto?> GetBookingByIdAsync(Guid userId, Guid bookingId, CancellationToken ct)
    {
        var booking = await _db.Bookings.AsNoTracking()
            .Include(b => b.Items)
            .FirstOrDefaultAsync(b => b.Id == bookingId && b.UserId == userId, ct);
        return booking is null ? null : BookingMapper.ToDto(booking);
    }

    public async Task<IReadOnlyList<BookingDto>> GetUserBookingsAsync(Guid userId, CancellationToken ct)
    {
        var bookings = await _db.Bookings.AsNoTracking()
            .Include(b => b.Items)
            .Where(b => b.UserId == userId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);
        return bookings.Select(BookingMapper.ToDto).ToList();
    }

    public async Task<BookingDto> ConfirmPaymentAsync(Guid userId, Guid bookingId, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var booking = await LockBookingAsync(bookingId, userId, ct)
            ?? throw new BookingNotFoundException(bookingId);

        if (booking.Status != BookingStatus.PendingPayment)
            throw new InvalidBookingStateException($"Cannot confirm payment for a booking in status {booking.Status}");
        if (booking.HoldExpiresAt < DateTimeOffset.UtcNow)
            throw new InvalidBookingStateException("Booking hold has expired");

        booking.Status = BookingStatus.Confirmed;
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return BookingMapper.ToDto(booking);
    }

    public async Task<BookingDto> CancelBookingAsync(Guid userId, Guid bookingId, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var booking = await LockBookingAsync(bookingId, userId, ct)
            ?? throw new BookingNotFoundException(bookingId);

        if (booking.Status != BookingStatus.PendingPayment)
            throw new InvalidBookingStateException($"Cannot cancel a booking in status {booking.Status}");

        await ReleaseInventoryAndVoucherAsync(booking, ct);
        booking.Status = BookingStatus.Cancelled;

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return BookingMapper.ToDto(booking);
    }

    public async Task<int> ExpireStaleHoldsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var staleIds = await _db.Bookings
            .Where(b => b.Status == BookingStatus.PendingPayment && b.HoldExpiresAt < now)
            .Select(b => b.Id)
            .ToListAsync(ct);

        var expiredCount = 0;
        foreach (var bookingId in staleIds)
        {
            // Clear the identity map before each booking so a stale, already-tracked
            // TicketCategory/Voucher instance from a previous iteration in this loop can
            // never be handed back instead of a freshly-locked row.
            _db.ChangeTracker.Clear();

            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            var booking = await LockBookingAsync(bookingId, userId: null, ct);
            if (booking is null || booking.Status != BookingStatus.PendingPayment) continue;

            await ReleaseInventoryAndVoucherAsync(booking, ct);
            booking.Status = BookingStatus.Expired;
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            expiredCount++;
        }
        return expiredCount;
    }

    /// <summary>
    /// Locks the Booking row itself (SELECT ... FOR UPDATE) as the first thing done inside
    /// the caller's transaction, before its Status is inspected or mutated. Without this,
    /// concurrent status transitions on the same booking (e.g. a user's CancelBookingAsync
    /// racing the stale-hold sweep's ExpireStaleHoldsAsync) can both read Status as
    /// PendingPayment before either commits, and both release inventory for the same
    /// booking, double-crediting AvailableQuantity.
    /// </summary>
    private async Task<Booking?> LockBookingAsync(Guid bookingId, Guid? userId, CancellationToken ct)
    {
        var booking = userId is Guid uid
            ? await _db.Bookings
                .FromSqlInterpolated($"SELECT * FROM \"Bookings\" WHERE \"Id\" = {bookingId} AND \"UserId\" = {uid} FOR UPDATE")
                .SingleOrDefaultAsync(ct)
            : await _db.Bookings
                .FromSqlInterpolated($"SELECT * FROM \"Bookings\" WHERE \"Id\" = {bookingId} FOR UPDATE")
                .SingleOrDefaultAsync(ct);

        if (booking is not null)
        {
            // Relies on EF Core's change-tracker relationship fixup: loading the BookingItem
            // rows for an already-tracked Booking automatically populates booking.Items,
            // without needing to compose .Include() on top of the raw-SQL FOR UPDATE query.
            await _db.BookingItems.Where(i => i.BookingId == booking.Id).LoadAsync(ct);
        }
        return booking;
    }

    private static void ValidateRequest(CreateBookingRequest request)
    {
        if (request.Items is null || request.Items.Count == 0)
            throw new InvalidBookingRequestException("At least one booking item is required");
        if (request.Items.Any(i => i.Quantity <= 0))
            throw new InvalidBookingRequestException("Quantity must be greater than zero for every item");
        if (request.Items.Select(i => i.TicketCategoryId).Distinct().Count() != request.Items.Count)
            throw new InvalidBookingRequestException("Duplicate ticket category in booking request");
    }

    private async Task ReleaseInventoryAndVoucherAsync(Booking booking, CancellationToken ct)
    {
        // Lock ticket categories in the same globally-consistent, ID-sorted order used by
        // CreateBookingAsync, so two concurrent releases (e.g. a cancel racing the sweep for
        // different bookings that share categories) can never deadlock against each other.
        foreach (var item in booking.Items.OrderBy(i => i.TicketCategoryId))
        {
            var category = await _db.TicketCategories
                .FromSqlInterpolated($"SELECT * FROM \"TicketCategories\" WHERE \"Id\" = {item.TicketCategoryId} FOR UPDATE")
                .SingleAsync(ct);
            category.AvailableQuantity += item.Quantity;
        }

        if (booking.VoucherId is not null)
        {
            var voucher = await _db.Vouchers
                .FromSqlInterpolated($"SELECT * FROM \"Vouchers\" WHERE \"Id\" = {booking.VoucherId} FOR UPDATE")
                .SingleAsync(ct);
            voucher.RedeemedCount -= 1;

            var redemption = await _db.VoucherRedemptions.FirstOrDefaultAsync(r => r.BookingId == booking.Id, ct);
            if (redemption is not null) _db.VoucherRedemptions.Remove(redemption);
        }
    }

    private static bool IsIdempotencyKeyConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pgEx &&
        pgEx.ConstraintName == "IX_Bookings_IdempotencyKey";
}
