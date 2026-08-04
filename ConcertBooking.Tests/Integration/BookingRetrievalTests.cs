using System.Net;
using System.Net.Http.Json;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using Xunit;

namespace ConcertBooking.Tests.Integration;

[Collection("Api")]
public class BookingRetrievalTests
{
    private readonly ApiFactory _factory;
    public BookingRetrievalTests(ApiFactory factory) => _factory = factory;

    private HttpClient ClientFor(Guid userId, string? idempotencyKey = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());
        if (idempotencyKey is not null) client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);
        return client;
    }

    [Fact]
    public async Task GetById_OwnBooking_ReturnsIt()
    {
        var createResponse = await ClientFor(DbSeeder.CustomerId, $"retrieval-{Guid.NewGuid()}")
            .PostAsJsonAsync("/api/bookings", new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, null));
        var created = await createResponse.Content.ReadFromJsonAsync<BookingDto>();

        var getResponse = await ClientFor(DbSeeder.CustomerId).GetAsync($"/api/bookings/{created!.Id}");

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task GetById_AnotherUsersBooking_Returns404()
    {
        var createResponse = await ClientFor(DbSeeder.CustomerId, $"retrieval-isolation-{Guid.NewGuid()}")
            .PostAsJsonAsync("/api/bookings", new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, null));
        var created = await createResponse.Content.ReadFromJsonAsync<BookingDto>();

        var getResponse = await ClientFor(DbSeeder.SecondCustomerId).GetAsync($"/api/bookings/{created!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task GetMine_OnlyReturnsCallersBookings()
    {
        await ClientFor(DbSeeder.SecondCustomerId, $"mine-{Guid.NewGuid()}")
            .PostAsJsonAsync("/api/bookings", new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, null));

        var response = await ClientFor(DbSeeder.SecondCustomerId).GetAsync("/api/bookings");
        var bookings = await response.Content.ReadFromJsonAsync<List<BookingDto>>();

        Assert.All(bookings!, b => Assert.Equal(DbSeeder.SecondCustomerId, b.UserId));
    }
}
