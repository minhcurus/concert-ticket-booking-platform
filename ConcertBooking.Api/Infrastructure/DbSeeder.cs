using ConcertBooking.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ConcertBooking.Api.Infrastructure;

public static class DbSeeder
{
    public static readonly Guid CustomerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid OperatorId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid ConcertId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid VipCategoryId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid StandardCategoryId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public static readonly Guid VoucherId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    public static readonly Guid SecondCustomerId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    public static async Task SeedAsync(AppDbContext db)
    {
        if (await db.Users.AnyAsync()) return;

        db.Users.AddRange(
            new User { Id = CustomerId, Name = "Alice Customer", Email = "alice@example.com", Role = UserRole.Customer },
            new User { Id = OperatorId, Name = "Bob Operator", Email = "bob@example.com", Role = UserRole.Operator },
            new User { Id = SecondCustomerId, Name = "Carol Customer", Email = "carol@example.com", Role = UserRole.Customer });

        db.Concerts.Add(new Concert
        {
            Id = ConcertId,
            Title = "Flash Sale Live",
            Description = "Launch week flagship concert",
            VenueName = "Downtown Arena",
            StartTime = DateTimeOffset.UtcNow.AddDays(30),
            Status = ConcertStatus.Published,
        });

        db.TicketCategories.AddRange(
            new TicketCategory { Id = VipCategoryId, ConcertId = ConcertId, Name = "VIP", Price = 250m, TotalQuantity = 50, AvailableQuantity = 50 },
            new TicketCategory { Id = StandardCategoryId, ConcertId = ConcertId, Name = "Standard", Price = 90m, TotalQuantity = 500, AvailableQuantity = 500 });

        db.Vouchers.Add(new Voucher
        {
            Id = VoucherId,
            Code = "LAUNCH10",
            DiscountType = VoucherDiscountType.Percentage,
            DiscountValue = 10m,
            MaxRedemptions = 100,
            RedeemedCount = 0,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(60),
            ConcertId = null,
        });

        await db.SaveChangesAsync();
    }
}
