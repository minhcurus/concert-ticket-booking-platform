using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Dtos;

namespace ConcertBooking.Api.Services;

public static class BookingMapper
{
    public static BookingDto ToDto(Booking booking) => new(
        booking.Id, booking.UserId, booking.ConcertId, booking.Status.ToString(), booking.HoldExpiresAt,
        booking.TotalAmount, booking.VoucherId, booking.IsFlagged, booking.FlagReason,
        booking.Items.Select(i => new BookingItemDto(i.TicketCategoryId, i.Quantity, i.UnitPrice)).ToList());
}
