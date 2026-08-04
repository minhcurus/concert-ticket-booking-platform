using ConcertBooking.Api.Services;
using StackExchange.Redis;

namespace ConcertBooking.Api.Infrastructure;

public class HoldExpirySweepService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private const string LockKey = "lock:hold-expiry-sweep";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<HoldExpirySweepService> _logger;

    public HoldExpirySweepService(IServiceScopeFactory scopeFactory, IConnectionMultiplexer redis, ILogger<HoldExpirySweepService> logger)
    {
        _scopeFactory = scopeFactory;
        _redis = redis;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await TrySweepAsync(stoppingToken);
            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    // A failed sweep cycle must never bring down the whole API — Redis is a coordination
    // optimization here, not a correctness guarantee (Postgres row-locking in
    // ExpireStaleHoldsAsync is the real backstop), so any transient failure just skips
    // this tick and retries on the next one. OperationCanceledException from shutdown is
    // allowed to propagate so the host can stop normally.
    private async Task TrySweepAsync(CancellationToken ct)
    {
        try
        {
            var redisDb = _redis.GetDatabase();
            var acquired = await redisDb.StringSetAsync(LockKey, "locked", TimeSpan.FromSeconds(25), When.NotExists);
            if (!acquired) return;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var bookingService = scope.ServiceProvider.GetRequiredService<IBookingService>();
                var expiredCount = await bookingService.ExpireStaleHoldsAsync(ct);
                if (expiredCount > 0)
                {
                    _logger.LogInformation("Hold-expiry sweep expired {Count} stale booking(s)", expiredCount);
                }
            }
            finally
            {
                try
                {
                    await redisDb.KeyDeleteAsync(LockKey);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Hold-expiry sweep: failed to release Redis leader lock");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Hold-expiry sweep cycle failed; will retry next interval");
        }
    }
}
