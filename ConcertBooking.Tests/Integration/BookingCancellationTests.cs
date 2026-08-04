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
public class BookingCancellationTests
{
    private readonly ApiFactory _factory;
    public BookingCancellationTests(ApiFactory factory) => _factory = factory;

    private HttpClient CustomerClient(string idempotencyKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", DbSeeder.CustomerId.ToString());
        client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);
        return client;
    }

    [Fact]
    public async Task Cancel_PendingBooking_ReleasesInventory()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var before = (await db.TicketCategories.SingleAsync(c => c.Id == DbSeeder.StandardCategoryId)).AvailableQuantity;

        var client = CustomerClient($"cancel-{Guid.NewGuid()}");
        var createResponse = await client.PostAsJsonAsync("/api/bookings", new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, null));
        var booking = await createResponse.Content.ReadFromJsonAsync<BookingDto>();

        var cancelResponse = await client.PostAsync($"/api/bookings/{booking!.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);
        var cancelled = await cancelResponse.Content.ReadFromJsonAsync<BookingDto>();
        Assert.Equal("Cancelled", cancelled!.Status);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var after = (await verifyDb.TicketCategories.SingleAsync(c => c.Id == DbSeeder.StandardCategoryId)).AvailableQuantity;
        Assert.Equal(before, after); // reserved 1, then released 1 — net zero
    }

    [Fact]
    public async Task Cancel_ReleasesVoucherRedemptionSlot()
    {
        // Uses a dedicated ad-hoc customer (rather than DbSeeder.CustomerId) so this test's
        // "the voucher redemption slot is available to redeem" assumption doesn't collide with
        // other tests in this collection (e.g. BookingCreationTests.Create_WithValidVoucher_
        // AppliesDiscountAndRecordsRedemption, which redeems LAUNCH10 as DbSeeder.CustomerId and
        // never cancels that booking) against the shared, never-reset DB fixture.
        var dedicatedUserId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User { Id = dedicatedUserId, Name = "Cancel Voucher Test Customer", Email = $"{dedicatedUserId}@example.com", Role = UserRole.Customer });
            await db.SaveChangesAsync();
        }

        HttpClient ClientFor(string idempotencyKey)
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", dedicatedUserId.ToString());
            client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);
            return client;
        }

        var client = ClientFor($"cancel-voucher-{Guid.NewGuid()}");
        var createResponse = await client.PostAsJsonAsync("/api/bookings", new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, "LAUNCH10"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var booking = await createResponse.Content.ReadFromJsonAsync<BookingDto>();
        var cancelResponse = await client.PostAsync($"/api/bookings/{booking!.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);

        // The same customer can redeem LAUNCH10 again after cancellation freed their redemption slot.
        var secondResponse = await ClientFor($"cancel-voucher-retry-{Guid.NewGuid()}")
            .PostAsJsonAsync("/api/bookings", new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, "LAUNCH10"));

        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
    }

    [Fact]
    public async Task Cancel_AlreadyConfirmedBooking_Returns409()
    {
        var client = CustomerClient($"cancel-confirmed-{Guid.NewGuid()}");
        var createResponse = await client.PostAsJsonAsync("/api/bookings", new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, null));
        var booking = await createResponse.Content.ReadFromJsonAsync<BookingDto>();
        await client.PostAsync($"/api/bookings/{booking!.Id}/confirm-payment", null);

        var cancelResponse = await client.PostAsync($"/api/bookings/{booking.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.Conflict, cancelResponse.StatusCode);
    }
}
