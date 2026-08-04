namespace ConcertBooking.Api.Services;

public interface IIdempotencyCache
{
    Task<Guid?> TryGetAsync(string idempotencyKey, CancellationToken ct);
    Task SetAsync(string idempotencyKey, Guid bookingId, TimeSpan ttl, CancellationToken ct);
}
