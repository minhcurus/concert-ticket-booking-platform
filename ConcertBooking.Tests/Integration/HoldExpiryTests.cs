using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using ConcertBooking.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using Xunit;

namespace ConcertBooking.Tests.Integration;

[Collection("Api")]
public class HoldExpiryTests
{
    private readonly ApiFactory _factory;
    public HoldExpiryTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ExpireStaleHolds_ReleasesInventoryAndMarksExpired()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", DbSeeder.CustomerId.ToString());
        client.DefaultRequestHeaders.Add("Idempotency-Key", $"expiry-{Guid.NewGuid()}");

        var createResponse = await client.PostAsJsonAsync("/api/bookings", new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, null));
        var booking = await createResponse.Content.ReadFromJsonAsync<BookingDto>();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var before = await db.TicketCategories.SingleAsync(c => c.Id == DbSeeder.StandardCategoryId);
            var beforeAvailable = before.AvailableQuantity;

            var entity = await db.Bookings.SingleAsync(b => b.Id == booking!.Id);
            entity.HoldExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();

            var bookingService = scope.ServiceProvider.GetRequiredService<IBookingService>();
            var expiredCount = await bookingService.ExpireStaleHoldsAsync(default);
            Assert.True(expiredCount >= 1);

            var refreshedBooking = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == booking!.Id);
            Assert.Equal(BookingStatus.Expired, refreshedBooking.Status);

            var after = await db.TicketCategories.AsNoTracking().SingleAsync(c => c.Id == DbSeeder.StandardCategoryId);
            Assert.Equal(beforeAvailable + 1, after.AvailableQuantity);
        }
    }
}
