namespace ConcertBooking.Api.Domain;

public class Booking
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ConcertId { get; set; }
    public BookingStatus Status { get; set; } = BookingStatus.PendingPayment;
    public string IdempotencyKey { get; set; } = default!;
    public DateTimeOffset HoldExpiresAt { get; set; }
    public decimal TotalAmount { get; set; }
    public Guid? VoucherId { get; set; }
    public bool IsFlagged { get; set; }
    public string? FlagReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<BookingItem> Items { get; set; } = new();
}
