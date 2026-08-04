using ConcertBooking.Api.Domain;

namespace ConcertBooking.Api.Services;

public static class VoucherDiscountCalculator
{
    public static decimal Apply(decimal amount, VoucherDiscountType type, decimal discountValue)
    {
        var discounted = type switch
        {
            VoucherDiscountType.Percentage => amount - amount * (discountValue / 100m),
            VoucherDiscountType.FixedAmount => amount - discountValue,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
        return discounted < 0 ? 0 : discounted;
    }
}
