using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Services;
using Xunit;

namespace ConcertBooking.Tests.Unit;

public class VoucherDiscountCalculatorTests
{
    [Fact]
    public void Apply_PercentageDiscount_ReducesAmountProportionally()
    {
        var result = VoucherDiscountCalculator.Apply(100m, VoucherDiscountType.Percentage, 10m);
        Assert.Equal(90m, result);
    }

    [Fact]
    public void Apply_FixedAmountDiscount_SubtractsFlatValue()
    {
        var result = VoucherDiscountCalculator.Apply(100m, VoucherDiscountType.FixedAmount, 30m);
        Assert.Equal(70m, result);
    }

    [Fact]
    public void Apply_DiscountExceedsAmount_ClampsToZero()
    {
        var result = VoucherDiscountCalculator.Apply(20m, VoucherDiscountType.FixedAmount, 50m);
        Assert.Equal(0m, result);
    }
}
