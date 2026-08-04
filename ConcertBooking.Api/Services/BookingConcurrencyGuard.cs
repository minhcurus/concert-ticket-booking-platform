using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ConcertBooking.Api.Services;

/// <summary>
/// Row-locking and inventory/voucher accounting shared by every path that can transition a
/// Booking's status (customer confirm/cancel, the hold-expiry sweep, and the operator's manual
/// override). Centralized here so these two guarantees can never silently drift between paths:
/// the Booking row itself must be locked before its Status is inspected or mutated, and
/// releasing/re-reserving inventory must always move the associated voucher redemption in
/// lockstep with it.
/// </summary>
internal static class BookingConcurrencyGuard
{
    /// <summary>
    /// Locks the Booking row itself (SELECT ... FOR UPDATE) as the first thing done inside the
    /// caller's transaction, before its Status is inspected or mutated. Without this, concurrent
    /// status transitions on the same booking (e.g. a user's cancel racing the stale-hold sweep,
    /// or an operator's override racing either) can both read Status as PendingPayment before
    /// either commits, and both release inventory for the same booking, double-crediting
    /// AvailableQuantity.
    /// </summary>
    public static async Task<Booking?> LockBookingAsync(AppDbContext db, Guid bookingId, Guid? userId, CancellationToken ct)
    {
        var booking = userId is Guid uid
            ? await db.Bookings
                .FromSqlInterpolated($"SELECT * FROM \"Bookings\" WHERE \"Id\" = {bookingId} AND \"UserId\" = {uid} FOR UPDATE")
                .SingleOrDefaultAsync(ct)
            : await db.Bookings
                .FromSqlInterpolated($"SELECT * FROM \"Bookings\" WHERE \"Id\" = {bookingId} FOR UPDATE")
                .SingleOrDefaultAsync(ct);

        if (booking is not null)
        {
            // Relies on EF Core's change-tracker relationship fixup: loading the BookingItem
            // rows for an already-tracked Booking automatically populates booking.Items,
            // without needing to compose .Include() on top of the raw-SQL FOR UPDATE query.
            await db.BookingItems.Where(i => i.BookingId == booking.Id).LoadAsync(ct);
        }
        return booking;
    }

    /// <summary>
    /// Releases a booking's reserved inventory and, if it used a voucher, the voucher redemption
    /// slot with it — the two must move together or a released booking can leave a voucher's
    /// redemption count permanently inflated and the customer's one-time use permanently burned.
    /// </summary>
    public static async Task ReleaseInventoryAndVoucherAsync(AppDbContext db, Booking booking, CancellationToken ct)
    {
        // Lock ticket categories in a globally-consistent, ID-sorted order so two concurrent
        // releases (e.g. a cancel racing the sweep, or two operator overrides, for different
        // bookings that share categories) can never deadlock against each other.
        foreach (var item in booking.Items.OrderBy(i => i.TicketCategoryId))
        {
            var category = await db.TicketCategories
                .FromSqlInterpolated($"SELECT * FROM \"TicketCategories\" WHERE \"Id\" = {item.TicketCategoryId} FOR UPDATE")
                .SingleAsync(ct);
            category.AvailableQuantity += item.Quantity;
        }

        if (booking.VoucherId is not null)
        {
            var voucher = await db.Vouchers
                .FromSqlInterpolated($"SELECT * FROM \"Vouchers\" WHERE \"Id\" = {booking.VoucherId} FOR UPDATE")
                .SingleAsync(ct);
            voucher.RedeemedCount -= 1;

            var redemption = await db.VoucherRedemptions.FirstOrDefaultAsync(r => r.BookingId == booking.Id, ct);
            if (redemption is not null) db.VoucherRedemptions.Remove(redemption);
        }
    }

    /// <summary>
    /// The symmetric counterpart to <see cref="ReleaseInventoryAndVoucherAsync"/>: re-reserves
    /// inventory and, if the booking originally used a voucher, re-creates the voucher redemption
    /// slot. Only reachable via the operator's manual status override (moving a Cancelled/Expired
    /// booking back to an active status) — the ordinary customer flow has no "un-cancel".
    /// </summary>
    public static async Task ReserveInventoryAndVoucherAsync(AppDbContext db, Booking booking, CancellationToken ct)
    {
        foreach (var item in booking.Items.OrderBy(i => i.TicketCategoryId))
        {
            var category = await db.TicketCategories
                .FromSqlInterpolated($"SELECT * FROM \"TicketCategories\" WHERE \"Id\" = {item.TicketCategoryId} FOR UPDATE")
                .SingleAsync(ct);
            if (category.AvailableQuantity < item.Quantity) throw new SoldOutException(item.TicketCategoryId);
            category.AvailableQuantity -= item.Quantity;
        }

        if (booking.VoucherId is not null)
        {
            var voucher = await db.Vouchers
                .FromSqlInterpolated($"SELECT * FROM \"Vouchers\" WHERE \"Id\" = {booking.VoucherId} FOR UPDATE")
                .SingleAsync(ct);
            if (voucher.RedeemedCount >= voucher.MaxRedemptions)
                throw new InvalidVoucherException("Voucher fully redeemed");

            voucher.RedeemedCount += 1;
            db.VoucherRedemptions.Add(new VoucherRedemption
            {
                Id = Guid.NewGuid(),
                VoucherId = voucher.Id,
                UserId = booking.UserId,
                BookingId = booking.Id,
            });
        }
    }
}
