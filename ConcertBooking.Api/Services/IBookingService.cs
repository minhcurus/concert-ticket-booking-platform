using ConcertBooking.Api.Dtos;

namespace ConcertBooking.Api.Services;

public interface IBookingService
{
    Task<BookingResult> CreateBookingAsync(Guid userId, CreateBookingRequest request, string idempotencyKey, CancellationToken ct);
    Task<BookingDto?> GetBookingByIdAsync(Guid userId, Guid bookingId, CancellationToken ct);
    Task<IReadOnlyList<BookingDto>> GetUserBookingsAsync(Guid userId, CancellationToken ct);
    Task<BookingDto> ConfirmPaymentAsync(Guid userId, Guid bookingId, CancellationToken ct);
    Task<BookingDto> CancelBookingAsync(Guid userId, Guid bookingId, CancellationToken ct);
    Task<int> ExpireStaleHoldsAsync(CancellationToken ct);
}
