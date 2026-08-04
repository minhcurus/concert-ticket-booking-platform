namespace ConcertBooking.Api.Domain;

public class Concert
{
    public Guid Id { get; set; }
    public string Title { get; set; } = default!;
    public string Description { get; set; } = default!;
    public string VenueName { get; set; } = default!;
    public DateTimeOffset StartTime { get; set; }
    public ConcertStatus Status { get; set; } = ConcertStatus.Draft;
    public List<TicketCategory> TicketCategories { get; set; } = new();
}
