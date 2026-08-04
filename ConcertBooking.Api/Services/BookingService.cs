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

        // All items must belong to a single, published concert. Without this, a basket mixing
        // categories from two concerts stores an arbitrary one as Booking.ConcertId (silently
        // hiding the other from operator concertId filters), and a voucher scoped to "concert A"
        // could be satisfied by a single cheap item from A while the rest of the basket is B's
        // tickets. A Draft/unpublished concert's categories must also not be bookable by ID even
        // though the customer catalog already hides them from browsing.
        var concertIds = categories.Select(c => c.ConcertId).Distinct().ToList();
        if (concertIds.Count > 1)
            throw new InvalidBookingRequestException("All items in a booking must belong to the same concert");

        var concertId = concertIds[0];
        var concert = await _db.Concerts.FirstOrDefaultAsync(c => c.Id == concertId, ct)
            ?? throw new ConcertNotFoundException(concertId);
        if (concert.Status != ConcertStatus.Published)
            throw new InvalidBookingRequestException("Cannot book tickets for a concert that is not published");

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

            await BookingConcurrencyGuard.ValidateVoucherForRedemptionAsync(_db, voucher, userId, concertId, ct);

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
            ConcertId = concertId,
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

        var booking = await BookingConcurrencyGuard.LockBookingAsync(_db, bookingId, userId, ct)
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

        var booking = await BookingConcurrencyGuard.LockBookingAsync(_db, bookingId, userId, ct)
            ?? throw new BookingNotFoundException(bookingId);

        if (booking.Status != BookingStatus.PendingPayment)
            throw new InvalidBookingStateException($"Cannot cancel a booking in status {booking.Status}");

        await BookingConcurrencyGuard.ReleaseInventoryAndVoucherAsync(_db, booking, ct);
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
            var booking = await BookingConcurrencyGuard.LockBookingAsync(_db, bookingId, userId: null, ct);
            if (booking is null || booking.Status != BookingStatus.PendingPayment) continue;

            await BookingConcurrencyGuard.ReleaseInventoryAndVoucherAsync(_db, booking, ct);
            booking.Status = BookingStatus.Expired;
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            expiredCount++;
        }
        return expiredCount;
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

    private static bool IsIdempotencyKeyConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pgEx &&
        pgEx.ConstraintName == "IX_Bookings_IdempotencyKey";
}
