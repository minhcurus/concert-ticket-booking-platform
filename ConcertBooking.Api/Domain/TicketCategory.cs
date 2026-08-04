namespace ConcertBooking.Api.Domain;

public class TicketCategory
{
    public Guid Id { get; set; }
    public Guid ConcertId { get; set; }
    public Concert Concert { get; set; } = default!;
    public string Name { get; set; } = default!;
    public decimal Price { get; set; }
    public int TotalQuantity { get; set; }
    public int AvailableQuantity { get; set; }
}
