using ConcertBooking.Api.Services;
using StackExchange.Redis;

namespace ConcertBooking.Api.Infrastructure;

public class HoldExpirySweepService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private const string LockKey = "lock:hold-expiry-sweep";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnectionMultiplexer _redis;

    public HoldExpirySweepService(IServiceScopeFactory scopeFactory, IConnectionMultiplexer redis)
    {
        _scopeFactory = scopeFactory;
        _redis = redis;
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
            catch (TaskCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    private async Task TrySweepAsync(CancellationToken ct)
    {
        var redisDb = _redis.GetDatabase();
        var token = Guid.NewGuid().ToString();
        var acquired = await redisDb.StringSetAsync(LockKey, token, TimeSpan.FromSeconds(25), When.NotExists);
        if (!acquired) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var bookingService = scope.ServiceProvider.GetRequiredService<IBookingService>();
            await bookingService.ExpireStaleHoldsAsync(ct);
        }
        finally
        {
            await redisDb.KeyDeleteAsync(LockKey);
        }
    }
}
