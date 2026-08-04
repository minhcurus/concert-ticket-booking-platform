namespace ConcertBooking.Api.Dtos;

public record CreateConcertRequest(string Title, string Description, string VenueName, DateTimeOffset StartTime);
public record UpdateConcertRequest(string Title, string Description, string VenueName, DateTimeOffset StartTime);
public record AddTicketCategoryRequest(string Name, decimal Price, int TotalQuantity);
