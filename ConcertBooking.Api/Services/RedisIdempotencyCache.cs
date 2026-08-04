using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ConcertBooking.Api.Services;

public class RedisIdempotencyCache : IIdempotencyCache
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisIdempotencyCache> _logger;

    public RedisIdempotencyCache(IConnectionMultiplexer redis, ILogger<RedisIdempotencyCache> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    private static string Key(string idempotencyKey) => $"idempotency:{idempotencyKey}";

    // This is a performance fast-path only — the DB unique constraint on IdempotencyKey is the
    // real source of truth (see BookingService.CreateBookingAsync's catch for IsIdempotencyKeyConflict).
    // A Redis fault here must never fail the request; degrading to "not cached" just means the
    // request falls through to Postgres, which is always correct, only slower.
    public async Task<Guid?> TryGetAsync(string idempotencyKey, CancellationToken ct)
    {
        try
        {
            var db = _redis.GetDatabase();
            var value = await db.StringGetAsync(Key(idempotencyKey));
            return value.IsNullOrEmpty ? null : Guid.Parse((string)value!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Catches RedisException (connection failures) and TimeoutException (RedisTimeoutException
            // derives from TimeoutException, not RedisException — a slow-but-up Redis under flash-sale
            // load must degrade exactly like a down one) and any other transient failure.
            _logger.LogWarning(ex, "Idempotency cache read failed, falling through to Postgres");
            return null;
        }
    }

    public async Task SetAsync(string idempotencyKey, Guid bookingId, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.StringSetAsync(Key(idempotencyKey), bookingId.ToString(), ttl, When.NotExists);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Idempotency cache write failed; the DB unique constraint remains the source of truth");
        }
    }
}
