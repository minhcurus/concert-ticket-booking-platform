using System.Text.Json;
using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ConcertBooking.Api.Services;

public class CatalogService : ICatalogService
{
    private static readonly TimeSpan ListCacheTtl = TimeSpan.FromSeconds(30);

    private readonly AppDbContext _db;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<CatalogService> _logger;

    public CatalogService(AppDbContext db, IConnectionMultiplexer redis, ILogger<CatalogService> logger)
    {
        _db = db;
        _redis = redis;
        _logger = logger;
    }

    // The Redis cache here is a read-through performance optimization only — Postgres is
    // always the source of truth. A Redis fault must degrade to querying the DB directly,
    // not fail the request.
    public async Task<IReadOnlyList<ConcertSummaryDto>> GetPublishedConcertsAsync(int page, int pageSize, CancellationToken ct)
    {
        var cacheKey = $"catalog:concerts:page:{page}:size:{pageSize}";

        try
        {
            var cached = await _redis.GetDatabase().StringGetAsync(cacheKey);
            if (!cached.IsNullOrEmpty)
                return JsonSerializer.Deserialize<List<ConcertSummaryDto>>((string)cached!)!;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Catches RedisException (connection failures), TimeoutException (RedisTimeoutException
            // derives from TimeoutException, not RedisException) and a malformed/schema-drifted
            // cached payload (JsonException) — any of these must fall through to Postgres, not fail.
            _logger.LogWarning(ex, "Catalog cache read failed, falling through to Postgres");
        }

        var concerts = await _db.Concerts
            .Where(c => c.Status == ConcertStatus.Published)
            .OrderBy(c => c.StartTime)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new ConcertSummaryDto(c.Id, c.Title, c.VenueName, c.StartTime))
            .ToListAsync(ct);

        try
        {
            await _redis.GetDatabase().StringSetAsync(cacheKey, JsonSerializer.Serialize(concerts), ListCacheTtl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Catalog cache write failed; serving uncached result");
        }

        return concerts;
    }

    public async Task<ConcertDetailDto?> GetPublishedConcertByIdAsync(Guid id, CancellationToken ct)
    {
        var concert = await _db.Concerts
            .Include(c => c.TicketCategories)
            .FirstOrDefaultAsync(c => c.Id == id && c.Status == ConcertStatus.Published, ct);

        return concert is null ? null : ToDetailDto(concert);
    }

    internal static ConcertDetailDto ToDetailDto(Concert concert) => new(
        concert.Id, concert.Title, concert.Description, concert.VenueName, concert.StartTime,
        concert.Status.ToString(),
        concert.TicketCategories.Select(t => new TicketCategoryDto(t.Id, t.Name, t.Price, t.AvailableQuantity)).ToList());
}
