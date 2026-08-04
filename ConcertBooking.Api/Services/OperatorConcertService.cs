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

    public async Task<ConcertDetailDto> CreateConcertAsync(CreateConcertRequest request, CancellationToken ct)
    {
        if (request.StartTime <= DateTimeOffset.UtcNow)
            throw new InvalidConcertRequestException("StartTime must be in the future");

        var concert = new Concert
        {
            Id = Guid.NewGuid(),
            Title = request.Title,
            Description = request.Description,
            VenueName = request.VenueName,
            StartTime = request.StartTime,
            Status = ConcertStatus.Draft,
        };
        _db.Concerts.Add(concert);
        await _db.SaveChangesAsync(ct);
        return CatalogService.ToDetailDto(concert);
    }

    public async Task<ConcertDetailDto> UpdateConcertAsync(Guid id, UpdateConcertRequest request, CancellationToken ct)
    {
        var concert = await _db.Concerts.Include(c => c.TicketCategories).FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new ConcertNotFoundException(id);

        if (request.StartTime <= DateTimeOffset.UtcNow)
            throw new InvalidConcertRequestException("StartTime must be in the future");

        concert.Title = request.Title;
        concert.Description = request.Description;
        concert.VenueName = request.VenueName;
        concert.StartTime = request.StartTime;
        await _db.SaveChangesAsync(ct);
        return CatalogService.ToDetailDto(concert);
    }

    public async Task<TicketCategoryDto> AddTicketCategoryAsync(Guid concertId, AddTicketCategoryRequest request, CancellationToken ct)
    {
        var concert = await _db.Concerts.FirstOrDefaultAsync(c => c.Id == concertId, ct)
            ?? throw new ConcertNotFoundException(concertId);

        if (request.Price < 0)
            throw new InvalidConcertRequestException("Price must not be negative");
        if (request.TotalQuantity <= 0)
            throw new InvalidConcertRequestException("TotalQuantity must be positive");

        var category = new TicketCategory
        {
            Id = Guid.NewGuid(),
            ConcertId = concertId,
            Name = request.Name,
            Price = request.Price,
            TotalQuantity = request.TotalQuantity,
            AvailableQuantity = request.TotalQuantity,
        };
        _db.TicketCategories.Add(category);
        await _db.SaveChangesAsync(ct);
        return new TicketCategoryDto(category.Id, category.Name, category.Price, category.AvailableQuantity);
    }

    public async Task<ConcertDetailDto> PublishConcertAsync(Guid id, CancellationToken ct)
    {
        var concert = await _db.Concerts.Include(c => c.TicketCategories).FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new ConcertNotFoundException(id);

        if (concert.TicketCategories.Count == 0)
            throw new ConcertNotPublishableException("Concert must have at least one ticket category before publishing");

        concert.Status = ConcertStatus.Published;
        await _db.SaveChangesAsync(ct);
        return CatalogService.ToDetailDto(concert);
    }
}
