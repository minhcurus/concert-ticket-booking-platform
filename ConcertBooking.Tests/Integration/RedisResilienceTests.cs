using ConcertBooking.Api.Infrastructure;
using ConcertBooking.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace ConcertBooking.Tests.Integration;

// Proves Redis is genuinely a performance optimization, not load-bearing: constructs the two
// Redis-backed services against a multiplexer pointed at nothing (rather than stopping the
// shared fixture's live Redis container, which every other test in this collection depends on),
// and confirms both degrade to Postgres instead of throwing.
[Collection("Api")]
public class RedisResilienceTests
{
    private readonly ApiFactory _factory;
    public RedisResilienceTests(ApiFactory factory) => _factory = factory;

    private static IConnectionMultiplexer BrokenMultiplexer() =>
        ConnectionMultiplexer.Connect("127.0.0.1:1,abortConnect=false,connectTimeout=200,connectRetry=0,syncTimeout=200");

    [Fact]
    public async Task IdempotencyCache_RedisUnavailable_TryGetReturnsNullInsteadOfThrowing()
    {
        using var broken = BrokenMultiplexer();
        var cache = new RedisIdempotencyCache(broken, NullLogger<RedisIdempotencyCache>.Instance);

        var result = await cache.TryGetAsync($"redis-down-{Guid.NewGuid()}", default);

        Assert.Null(result);
    }

    [Fact]
    public async Task IdempotencyCache_RedisUnavailable_SetDoesNotThrow()
    {
        using var broken = BrokenMultiplexer();
        var cache = new RedisIdempotencyCache(broken, NullLogger<RedisIdempotencyCache>.Instance);

        // No exception means the fast-path degraded correctly; the DB unique constraint on
        // IdempotencyKey remains the real source of truth regardless.
        await cache.SetAsync($"redis-down-{Guid.NewGuid()}", Guid.NewGuid(), TimeSpan.FromMinutes(5), default);
    }

    [Fact]
    public async Task CatalogService_RedisUnavailable_FallsThroughToPostgres()
    {
        using var broken = BrokenMultiplexer();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var catalogService = new CatalogService(db, broken, NullLogger<CatalogService>.Instance);

        var concerts = await catalogService.GetPublishedConcertsAsync(1, 20, default);

        Assert.Contains(concerts, c => c.Id == DbSeeder.ConcertId);
    }
}
