using System.Net;
using System.Net.Http.Json;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using Xunit;

namespace ConcertBooking.Tests.Integration;

[Collection("Api")]
public class PaymentConfirmationTests
{
    private readonly ApiFactory _factory;
    public PaymentConfirmationTests(ApiFactory factory) => _factory = factory;

    private HttpClient CustomerClient(string idempotencyKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", DbSeeder.CustomerId.ToString());
        client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);
        return client;
    }

    [Fact]
    public async Task ConfirmPayment_PendingBooking_TransitionsToConfirmed()
    {
        var client = CustomerClient($"confirm-{Guid.NewGuid()}");
        var createResponse = await client.PostAsJsonAsync("/api/bookings", new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, null));
        var booking = await createResponse.Content.ReadFromJsonAsync<BookingDto>();

        var confirmResponse = await client.PostAsync($"/api/bookings/{booking!.Id}/confirm-payment", null);

        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);
        var confirmed = await confirmResponse.Content.ReadFromJsonAsync<BookingDto>();
        Assert.Equal("Confirmed", confirmed!.Status);
    }

    [Fact]
    public async Task ConfirmPayment_AlreadyConfirmed_Returns409()
    {
        var client = CustomerClient($"confirm-twice-{Guid.NewGuid()}");
        var createResponse = await client.PostAsJsonAsync("/api/bookings", new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, null));
        var booking = await createResponse.Content.ReadFromJsonAsync<BookingDto>();
        await client.PostAsync($"/api/bookings/{booking!.Id}/confirm-payment", null);

        var secondConfirm = await client.PostAsync($"/api/bookings/{booking.Id}/confirm-payment", null);

        Assert.Equal(HttpStatusCode.Conflict, secondConfirm.StatusCode);
    }

    [Fact]
    public async Task ConfirmPayment_UnknownBooking_Returns404()
    {
        var client = CustomerClient($"confirm-unknown-{Guid.NewGuid()}");
        var response = await client.PostAsync($"/api/bookings/{Guid.NewGuid()}/confirm-payment", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
