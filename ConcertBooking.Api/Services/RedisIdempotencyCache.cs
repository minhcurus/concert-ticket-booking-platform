using StackExchange.Redis;

namespace ConcertBooking.Api.Services;

public class RedisIdempotencyCache : IIdempotencyCache
{
    private readonly IConnectionMultiplexer _redis;
    public RedisIdempotencyCache(IConnectionMultiplexer redis) => _redis = redis;

    private static string Key(string idempotencyKey) => $"idempotency:{idempotencyKey}";

    public async Task<Guid?> TryGetAsync(string idempotencyKey, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var value = await db.StringGetAsync(Key(idempotencyKey));
        return value.IsNullOrEmpty ? null : Guid.Parse((string)value!);
    }

    public async Task SetAsync(string idempotencyKey, Guid bookingId, TimeSpan ttl, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        await db.StringSetAsync(Key(idempotencyKey), bookingId.ToString(), ttl, When.NotExists);
    }
}
