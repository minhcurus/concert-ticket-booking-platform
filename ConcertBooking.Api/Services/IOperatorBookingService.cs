using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Dtos;

namespace ConcertBooking.Api.Services;

public interface IOperatorBookingService
{
    Task<IReadOnlyList<BookingDto>> GetBookingsAsync(BookingStatus? statusFilter, Guid? concertIdFilter, bool? isFlaggedFilter, CancellationToken ct);
    Task<BookingDto> OverrideStatusAsync(Guid bookingId, BookingStatus newStatus, CancellationToken ct);
    Task<BookingDto> FlagBookingAsync(Guid bookingId, string reason, CancellationToken ct);
}
