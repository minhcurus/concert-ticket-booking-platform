using System.Net;
using System.Net.Http.Json;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ConcertBooking.Tests.Integration;

[Collection("Api")]
public class OperatorBookingTests
{
    private readonly ApiFactory _factory;
    public OperatorBookingTests(ApiFactory factory) => _factory = factory;

    private HttpClient CustomerClient(string idempotencyKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", DbSeeder.CustomerId.ToString());
        client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);
        return client;
    }

    private HttpClient OperatorClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", DbSeeder.OperatorId.ToString());
        return client;
    }

    [Fact]
    public async Task Flag_SetsIsFlaggedAndReason()
    {
        var createResponse = await CustomerClient($"flag-{Guid.NewGuid()}")
            .PostAsJsonAsync("/api/bookings", new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, null));
        var booking = await createResponse.Content.ReadFromJsonAsync<BookingDto>();

        var flagResponse = await OperatorClient().PostAsJsonAsync($"/api/operator/bookings/{booking!.Id}/flag", new FlagBookingRequest("Suspected bot activity"));

        Assert.Equal(HttpStatusCode.OK, flagResponse.StatusCode);
        var flagged = await flagResponse.Content.ReadFromJsonAsync<BookingDto>();
        Assert.True(flagged!.IsFlagged);
        Assert.Equal("Suspected bot activity", flagged.FlagReason);
    }

    [Fact]
    public async Task OverrideStatus_ForceCancel_ReleasesInventory()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var before = (await db.TicketCategories.SingleAsync(c => c.Id == DbSeeder.StandardCategoryId)).AvailableQuantity;

        var createResponse = await CustomerClient($"override-{Guid.NewGuid()}")
            .PostAsJsonAsync("/api/bookings", new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, null));
        var booking = await createResponse.Content.ReadFromJsonAsync<BookingDto>();

        var overrideResponse = await OperatorClient().PutAsJsonAsync($"/api/operator/bookings/{booking!.Id}/status", new UpdateBookingStatusRequest("Cancelled"));

        Assert.Equal(HttpStatusCode.OK, overrideResponse.StatusCode);
        var updated = await overrideResponse.Content.ReadFromJsonAsync<BookingDto>();
        Assert.Equal("Cancelled", updated!.Status);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var after = (await verifyDb.TicketCategories.SingleAsync(c => c.Id == DbSeeder.StandardCategoryId)).AvailableQuantity;
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task List_FiltersByFlagged()
    {
        var createResponse = await CustomerClient($"filter-{Guid.NewGuid()}")
            .PostAsJsonAsync("/api/bookings", new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, null));
        var booking = await createResponse.Content.ReadFromJsonAsync<BookingDto>();
        await OperatorClient().PostAsJsonAsync($"/api/operator/bookings/{booking!.Id}/flag", new FlagBookingRequest("Test"));

        var listResponse = await OperatorClient().GetAsync("/api/operator/bookings?isFlagged=true");
        var flaggedBookings = await listResponse.Content.ReadFromJsonAsync<List<BookingDto>>();

        Assert.Contains(flaggedBookings!, b => b.Id == booking.Id);
        Assert.All(flaggedBookings!, b => Assert.True(b.IsFlagged));
    }
}
