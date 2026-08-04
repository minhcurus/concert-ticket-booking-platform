using System.Text.Json;
using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace ConcertBooking.Api.Services;

public class CatalogService : ICatalogService
{
    private static readonly TimeSpan ListCacheTtl = TimeSpan.FromSeconds(30);

    private readonly AppDbContext _db;
    private readonly IConnectionMultiplexer _redis;

    public CatalogService(AppDbContext db, IConnectionMultiplexer redis)
    {
        _db = db;
        _redis = redis;
    }

    public async Task<IReadOnlyList<ConcertSummaryDto>> GetPublishedConcertsAsync(int page, int pageSize, CancellationToken ct)
    {
        var redisDb = _redis.GetDatabase();
        var cacheKey = $"catalog:concerts:page:{page}:size:{pageSize}";

        var cached = await redisDb.StringGetAsync(cacheKey);
        if (!cached.IsNullOrEmpty)
            return JsonSerializer.Deserialize<List<ConcertSummaryDto>>((string)cached!)!;

        var concerts = await _db.Concerts
            .Where(c => c.Status == ConcertStatus.Published)
            .OrderBy(c => c.StartTime)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new ConcertSummaryDto(c.Id, c.Title, c.VenueName, c.StartTime))
            .ToListAsync(ct);

        await redisDb.StringSetAsync(cacheKey, JsonSerializer.Serialize(concerts), ListCacheTtl);
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
