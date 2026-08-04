using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Dtos;

namespace ConcertBooking.Api.Services;

public interface IOperatorConcertService
{
    Task<IReadOnlyList<ConcertDetailDto>> GetAllConcertsAsync(ConcertStatus? statusFilter, CancellationToken ct);
    Task<ConcertDetailDto?> GetConcertByIdAsync(Guid id, CancellationToken ct);
}
