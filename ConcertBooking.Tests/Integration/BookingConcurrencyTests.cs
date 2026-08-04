using System.Net;
using System.Net.Http.Json;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ConcertBooking.Tests.Integration;

[Collection("Api")]
public class BookingConcurrencyTests
{
    private readonly ApiFactory _factory;
    public BookingConcurrencyTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task CreateBooking_ConcurrentRequests_NeverOversells()
    {
        // Reset the VIP category to a small, known quantity dedicated to this test
        // (Standard and the LAUNCH10 voucher are used by BookingCreationTests, so this
        // test uses VIP to avoid any shared-state interference).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var category = await db.TicketCategories.SingleAsync(c => c.Id == DbSeeder.VipCategoryId);
            category.AvailableQuantity = 10;
            category.TotalQuantity = 10;
            await db.SaveChangesAsync();
        }

        const int concurrentRequests = 5;
        const int quantityPerRequest = 3; // 5 * 3 = 15 requested against 10 available

        var tasks = Enumerable.Range(0, concurrentRequests).Select(async i =>
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", DbSeeder.CustomerId.ToString());
            client.DefaultRequestHeaders.Add("Idempotency-Key", $"concurrency-{i}-{Guid.NewGuid()}");

            var request = new CreateBookingRequest(
                new List<CreateBookingItemRequest> { new(DbSeeder.VipCategoryId, quantityPerRequest) }, null);

            return await client.PostAsJsonAsync("/api/bookings", request);
        }).ToList();

        var responses = await Task.WhenAll(tasks);

        var successCount = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var soldOutCount = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(concurrentRequests, successCount + soldOutCount);
        Assert.True(successCount * quantityPerRequest <= 10, $"Oversold: {successCount} bookings of {quantityPerRequest} tickets each exceeds 10 available");

        // Requests serialize on the TicketCategory row lock, so allocation is deterministic
        // and order-independent: 10 -> 7 -> 4 -> 1, then the remaining two requests cannot fit.
        // Pinning this (rather than deriving the expectation from successCount) ensures the test
        // fails if the booking path starts rejecting every request outright.
        Assert.Equal(3, successCount);
        Assert.Equal(2, soldOutCount);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var finalCategory = await verifyDb.TicketCategories.SingleAsync(c => c.Id == DbSeeder.VipCategoryId);
        Assert.True(finalCategory.AvailableQuantity >= 0);
        Assert.Equal(1, finalCategory.AvailableQuantity);
    }

    [Fact]
    public async Task CreateBooking_ConcurrentDuplicateIdempotencyKey_CreatesExactlyOneBooking()
    {
        var idempotencyKey = $"dup-concurrent-{Guid.NewGuid()}";
        var request = new CreateBookingRequest(
            new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, null);

        var tasks = Enumerable.Range(0, 5).Select(_ =>
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", DbSeeder.CustomerId.ToString());
            client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);
            return client.PostAsJsonAsync("/api/bookings", request);
        }).ToList();

        var responses = await Task.WhenAll(tasks);
        var bookingIds = new List<Guid>();
        foreach (var response in responses)
        {
            var body = await response.Content.ReadFromJsonAsync<BookingDto>();
            bookingIds.Add(body!.Id);
        }

        Assert.Single(bookingIds.Distinct());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var matchingBookings = await db.Bookings.CountAsync(b => b.IdempotencyKey == idempotencyKey);
        Assert.Equal(1, matchingBookings);
    }
}
