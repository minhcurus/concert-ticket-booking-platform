using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ConcertBooking.Tests.Integration;

[Collection("Api")]
public class DbSeederTests
{
    private readonly ApiFactory _factory;
    public DbSeederTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Seed_CreatesExpectedUsersConcertCategoriesAndVoucher()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var customer = await db.Users.SingleAsync(u => u.Id == DbSeeder.CustomerId);
        var op = await db.Users.SingleAsync(u => u.Id == DbSeeder.OperatorId);
        Assert.Equal(UserRole.Customer, customer.Role);
        Assert.Equal(UserRole.Operator, op.Role);

        var concert = await db.Concerts.SingleAsync(c => c.Id == DbSeeder.ConcertId);
        Assert.Equal(ConcertStatus.Published, concert.Status);

        var categories = await db.TicketCategories.Where(c => c.ConcertId == DbSeeder.ConcertId).ToListAsync();
        Assert.Equal(2, categories.Count);

        var voucher = await db.Vouchers.SingleAsync(v => v.Id == DbSeeder.VoucherId);
        Assert.Equal("LAUNCH10", voucher.Code);
    }
}
