namespace ConcertBooking.Api.Domain;

public class Voucher
{
    public Guid Id { get; set; }
    public string Code { get; set; } = default!;
    public VoucherDiscountType DiscountType { get; set; }
    public decimal DiscountValue { get; set; }
    public int MaxRedemptions { get; set; }
    public int RedeemedCount { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset ValidTo { get; set; }
    public Guid? ConcertId { get; set; }
}
