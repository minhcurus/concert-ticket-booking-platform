using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ConcertBooking.Api.Services;

public class OperatorBookingService : IOperatorBookingService
{
    private readonly AppDbContext _db;
    public OperatorBookingService(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<BookingDto>> GetBookingsAsync(BookingStatus? statusFilter, Guid? concertIdFilter, bool? isFlaggedFilter, CancellationToken ct)
    {
        var query = _db.Bookings.Include(b => b.Items).AsQueryable();
        if (statusFilter is not null) query = query.Where(b => b.Status == statusFilter);
        if (concertIdFilter is not null) query = query.Where(b => b.ConcertId == concertIdFilter);
        if (isFlaggedFilter is not null) query = query.Where(b => b.IsFlagged == isFlaggedFilter);

        var bookings = await query.OrderByDescending(b => b.CreatedAt).ToListAsync(ct);
        return bookings.Select(BookingMapper.ToDto).ToList();
    }

    public async Task<BookingDto> OverrideStatusAsync(Guid bookingId, BookingStatus newStatus, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        // Locks the Booking row before Status is inspected, exactly like the customer-facing
        // confirm/cancel/expiry paths — without this, this override could race the hold-expiry
        // sweep (or another override) and double-credit inventory for the same booking.
        var booking = await BookingConcurrencyGuard.LockBookingAsync(_db, bookingId, userId: null, ct)
            ?? throw new BookingNotFoundException(bookingId);

        var wasReleasingStatus = booking.Status is BookingStatus.Cancelled or BookingStatus.Expired;
        var isReleasingStatus = newStatus is BookingStatus.Cancelled or BookingStatus.Expired;

        if (!wasReleasingStatus && isReleasingStatus)
        {
            await BookingConcurrencyGuard.ReleaseInventoryAndVoucherAsync(_db, booking, ct);
        }
        else if (wasReleasingStatus && !isReleasingStatus)
        {
            await BookingConcurrencyGuard.ReserveInventoryAndVoucherAsync(_db, booking, ct);
        }

        // Any booking with HoldExpiresAt in the past — which includes every Cancelled/Expired
        // booking, but also a Confirmed booking overridden back to PendingPayment more than 10
        // minutes after it was created — would otherwise be immediately re-expired by the very
        // next sweep tick, silently undoing this override within ~30s. Refresh unconditionally
        // whenever the target status is PendingPayment, not just on the releasing-status branch.
        if (newStatus == BookingStatus.PendingPayment)
            booking.HoldExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

        booking.Status = newStatus;
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return BookingMapper.ToDto(booking);
    }

    public async Task<BookingDto> FlagBookingAsync(Guid bookingId, string reason, CancellationToken ct)
    {
        var booking = await _db.Bookings.Include(b => b.Items).FirstOrDefaultAsync(b => b.Id == bookingId, ct)
            ?? throw new BookingNotFoundException(bookingId);

        booking.IsFlagged = true;
        booking.FlagReason = reason;
        await _db.SaveChangesAsync(ct);
        return BookingMapper.ToDto(booking);
    }
}
