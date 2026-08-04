using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ConcertBooking.Api.Services;

public class OperatorConcertService : IOperatorConcertService
{
    private readonly AppDbContext _db;
    public OperatorConcertService(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<ConcertDetailDto>> GetAllConcertsAsync(ConcertStatus? statusFilter, CancellationToken ct)
    {
        var query = _db.Concerts.Include(c => c.TicketCategories).AsQueryable();
        if (statusFilter is not null) query = query.Where(c => c.Status == statusFilter);

        var concerts = await query.OrderBy(c => c.StartTime).ToListAsync(ct);
        return concerts.Select(CatalogService.ToDetailDto).ToList();
    }

    public async Task<ConcertDetailDto?> GetConcertByIdAsync(Guid id, CancellationToken ct)
    {
        var concert = await _db.Concerts.Include(c => c.TicketCategories).FirstOrDefaultAsync(c => c.Id == id, ct);
        return concert is null ? null : CatalogService.ToDetailDto(concert);
    }
}
