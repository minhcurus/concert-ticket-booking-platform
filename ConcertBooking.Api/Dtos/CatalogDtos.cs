namespace ConcertBooking.Api.Dtos;

public record TicketCategoryDto(Guid Id, string Name, decimal Price, int AvailableQuantity);

public record ConcertSummaryDto(Guid Id, string Title, string VenueName, DateTimeOffset StartTime);

public record ConcertDetailDto(
    Guid Id, string Title, string Description, string VenueName, DateTimeOffset StartTime,
    string Status, List<TicketCategoryDto> TicketCategories);
