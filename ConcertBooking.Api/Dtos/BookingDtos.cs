namespace ConcertBooking.Api.Dtos;

public record CreateBookingItemRequest(Guid TicketCategoryId, int Quantity);
public record CreateBookingRequest(List<CreateBookingItemRequest> Items, string? VoucherCode);

public record BookingItemDto(Guid TicketCategoryId, int Quantity, decimal UnitPrice);

public record BookingDto(
    Guid Id, Guid UserId, Guid ConcertId, string Status, DateTimeOffset HoldExpiresAt,
    decimal TotalAmount, Guid? VoucherId, bool IsFlagged, string? FlagReason,
    List<BookingItemDto> Items);

public record BookingResult(BookingDto Booking, bool WasIdempotentReplay);
