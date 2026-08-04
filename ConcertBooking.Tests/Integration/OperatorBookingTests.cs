using System.Net;
using System.Net.Http.Json;
using ConcertBooking.Api.Domain;
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
    public async Task OverrideStatus_ForceCancelThenReinstateVoucherBooking_RestoresRedemption()
    {
        // Uses a dedicated ad-hoc customer (rather than DbSeeder.CustomerId) so this test's
        // "the voucher redemption slot is available to redeem" assumption doesn't collide with
        // other tests in this collection against the shared, never-reset DB fixture — same
        // pattern as BookingCancellationTests.Cancel_ReleasesVoucherRedemptionSlot.
        var dedicatedUserId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User { Id = dedicatedUserId, Name = "Override Voucher Test Customer", Email = $"{dedicatedUserId}@example.com", Role = UserRole.Customer });
            await db.SaveChangesAsync();
        }

        HttpClient ClientFor(string idempotencyKey)
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", dedicatedUserId.ToString());
            client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);
            return client;
        }

        var operatorClient = OperatorClient();

        var createResponse = await ClientFor($"override-voucher-{Guid.NewGuid()}")
            .PostAsJsonAsync("/api/bookings", new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, "LAUNCH10"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var booking = await createResponse.Content.ReadFromJsonAsync<BookingDto>();

        var cancelResponse = await operatorClient.PutAsJsonAsync($"/api/operator/bookings/{booking!.Id}/status", new UpdateBookingStatusRequest("Cancelled"));
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var redemptionAfterCancel = await db.VoucherRedemptions.SingleOrDefaultAsync(r => r.BookingId == booking.Id);
            Assert.Null(redemptionAfterCancel);
        }

        var reinstateResponse = await operatorClient.PutAsJsonAsync($"/api/operator/bookings/{booking.Id}/status", new UpdateBookingStatusRequest("PendingPayment"));
        Assert.Equal(HttpStatusCode.OK, reinstateResponse.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var redemptionAfterReinstate = await verifyDb.VoucherRedemptions.SingleOrDefaultAsync(r => r.BookingId == booking.Id);
        Assert.NotNull(redemptionAfterReinstate);
    }

    [Fact]
    public async Task OverrideStatus_ConfirmedToPendingPayment_RefreshesHoldExpiresAt()
    {
        var client = CustomerClient($"override-confirmed-{Guid.NewGuid()}");
        var createResponse = await client.PostAsJsonAsync("/api/bookings", new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, null));
        var booking = await createResponse.Content.ReadFromJsonAsync<BookingDto>();

        var confirmResponse = await client.PostAsync($"/api/bookings/{booking!.Id}/confirm-payment", null);
        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);

        // A Confirmed booking's HoldExpiresAt is whatever it was at creation time (10 minutes
        // out) and is never touched again — simulate one confirmed long enough ago that,
        // without the fix, overriding it back to PendingPayment would leave it immediately
        // eligible for the next sweep tick to re-expire.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var entity = await db.Bookings.SingleAsync(b => b.Id == booking.Id);
            entity.HoldExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-30);
            await db.SaveChangesAsync();
        }

        var overrideResponse = await OperatorClient().PutAsJsonAsync($"/api/operator/bookings/{booking.Id}/status", new UpdateBookingStatusRequest("PendingPayment"));
        Assert.Equal(HttpStatusCode.OK, overrideResponse.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var refreshed = await verifyDb.Bookings.AsNoTracking().SingleAsync(b => b.Id == booking.Id);
        Assert.True(refreshed.HoldExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5));
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
