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

        var booking = await _db.Bookings.Include(b => b.Items).FirstOrDefaultAsync(b => b.Id == bookingId, ct)
            ?? throw new BookingNotFoundException(bookingId);

        var wasReleasingStatus = booking.Status is BookingStatus.Cancelled or BookingStatus.Expired;
        var isReleasingStatus = newStatus is BookingStatus.Cancelled or BookingStatus.Expired;

        if (!wasReleasingStatus && isReleasingStatus)
        {
            foreach (var item in booking.Items)
            {
                var category = await _db.TicketCategories
                    .FromSqlInterpolated($"SELECT * FROM \"TicketCategories\" WHERE \"Id\" = {item.TicketCategoryId} FOR UPDATE")
                    .SingleAsync(ct);
                category.AvailableQuantity += item.Quantity;
            }
        }
        else if (wasReleasingStatus && !isReleasingStatus)
        {
            foreach (var item in booking.Items)
            {
                var category = await _db.TicketCategories
                    .FromSqlInterpolated($"SELECT * FROM \"TicketCategories\" WHERE \"Id\" = {item.TicketCategoryId} FOR UPDATE")
                    .SingleAsync(ct);
                if (category.AvailableQuantity < item.Quantity) throw new SoldOutException(item.TicketCategoryId);
                category.AvailableQuantity -= item.Quantity;
            }
        }

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
