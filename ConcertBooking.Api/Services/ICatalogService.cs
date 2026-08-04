using ConcertBooking.Api.Dtos;

namespace ConcertBooking.Api.Services;

public interface ICatalogService
{
    Task<IReadOnlyList<ConcertSummaryDto>> GetPublishedConcertsAsync(int page, int pageSize, CancellationToken ct);
    Task<ConcertDetailDto?> GetPublishedConcertByIdAsync(Guid id, CancellationToken ct);
}
