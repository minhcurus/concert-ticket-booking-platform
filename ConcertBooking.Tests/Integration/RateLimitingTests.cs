using System.Net;
using System.Net.Http.Json;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using Xunit;

namespace ConcertBooking.Tests.Integration;

[Collection("Api")]
public class RateLimitingTests
{
    private readonly ApiFactory _factory;
    public RateLimitingTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task CreateBooking_ExceedsPerUserLimit_EventuallyReturns429()
    {
        var rateLimitTestUserId = Guid.NewGuid().ToString();
        var request = new CreateBookingRequest(new List<CreateBookingItemRequest> { new(Guid.NewGuid(), 1) }, null);

        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 11; i++)
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", rateLimitTestUserId);
            client.DefaultRequestHeaders.Add("Idempotency-Key", $"rate-limit-{i}-{Guid.NewGuid()}");
            responses.Add(await client.PostAsJsonAsync("/api/bookings", request));
        }

        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.True(responses.Count(r => r.StatusCode != HttpStatusCode.TooManyRequests) <= 10);
    }
}
