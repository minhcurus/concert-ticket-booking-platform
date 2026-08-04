using ConcertBooking.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ConcertBooking.Tests.Integration;

[Collection("Api")]
public class RedisIdempotencyCacheTests
{
    private readonly ApiFactory _factory;
    public RedisIdempotencyCacheTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task SetThenGet_ReturnsSameBookingId()
    {
        using var scope = _factory.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IIdempotencyCache>();
        var key = $"test-{Guid.NewGuid()}";
        var bookingId = Guid.NewGuid();

        await cache.SetAsync(key, bookingId, TimeSpan.FromMinutes(5), default);
        var result = await cache.TryGetAsync(key, default);

        Assert.Equal(bookingId, result);
    }

    [Fact]
    public async Task Get_UnknownKey_ReturnsNull()
    {
        using var scope = _factory.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IIdempotencyCache>();
        var result = await cache.TryGetAsync($"missing-{Guid.NewGuid()}", default);
        Assert.Null(result);
    }

    [Fact]
    public async Task SetTwice_SecondWriteIgnored()
    {
        using var scope = _factory.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IIdempotencyCache>();
        var key = $"test-{Guid.NewGuid()}";
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        await cache.SetAsync(key, firstId, TimeSpan.FromMinutes(5), default);
        await cache.SetAsync(key, secondId, TimeSpan.FromMinutes(5), default);
        var result = await cache.TryGetAsync(key, default);

        Assert.Equal(firstId, result);
    }
}
