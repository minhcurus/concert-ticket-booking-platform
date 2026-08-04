using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ConcertBooking.Tests.Integration;

[Collection("Api")]
public class DbConstraintTests
{
    private readonly ApiFactory _factory;
    public DbConstraintTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task AvailableQuantity_CannotGoNegative()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var concert = new Concert { Id = Guid.NewGuid(), Title = "T", Description = "D", VenueName = "V", StartTime = DateTimeOffset.UtcNow, Status = ConcertStatus.Published };
        db.Concerts.Add(concert);
        var category = new TicketCategory { Id = Guid.NewGuid(), ConcertId = concert.Id, Name = "VIP", Price = 10m, TotalQuantity = 5, AvailableQuantity = -1 };
        db.TicketCategories.Add(category);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task IdempotencyKey_MustBeUniqueAcrossBookings()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var concert = new Concert { Id = Guid.NewGuid(), Title = "T2", Description = "D", VenueName = "V", StartTime = DateTimeOffset.UtcNow, Status = ConcertStatus.Published };
        db.Concerts.Add(concert);
        var userId = Guid.NewGuid();
        var key = $"dup-{Guid.NewGuid()}";

        db.Bookings.Add(new Booking { Id = Guid.NewGuid(), UserId = userId, ConcertId = concert.Id, IdempotencyKey = key, HoldExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), TotalAmount = 10m });
        await db.SaveChangesAsync();

        db.Bookings.Add(new Booking { Id = Guid.NewGuid(), UserId = userId, ConcertId = concert.Id, IdempotencyKey = key, HoldExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), TotalAmount = 10m });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task VoucherRedemption_OnePerUserPerVoucher()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var voucher = new Voucher { Id = Guid.NewGuid(), Code = $"V-{Guid.NewGuid()}", DiscountType = VoucherDiscountType.Percentage, DiscountValue = 10m, MaxRedemptions = 100, ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(1) };
        db.Vouchers.Add(voucher);
        var userId = Guid.NewGuid();

        db.VoucherRedemptions.Add(new VoucherRedemption { Id = Guid.NewGuid(), VoucherId = voucher.Id, BookingId = Guid.NewGuid(), UserId = userId });
        await db.SaveChangesAsync();

        db.VoucherRedemptions.Add(new VoucherRedemption { Id = Guid.NewGuid(), VoucherId = voucher.Id, BookingId = Guid.NewGuid(), UserId = userId });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
