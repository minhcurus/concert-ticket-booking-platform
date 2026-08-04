using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ConcertBooking.Api.Services;

public class CatalogService : ICatalogService
{
    private readonly AppDbContext _db;
    public CatalogService(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<ConcertSummaryDto>> GetPublishedConcertsAsync(int page, int pageSize, CancellationToken ct)
    {
        return await _db.Concerts
            .Where(c => c.Status == ConcertStatus.Published)
            .OrderBy(c => c.StartTime)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new ConcertSummaryDto(c.Id, c.Title, c.VenueName, c.StartTime))
            .ToListAsync(ct);
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
