using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Dtos;

namespace ConcertBooking.Api.Services;

public interface IOperatorConcertService
{
    Task<IReadOnlyList<ConcertDetailDto>> GetAllConcertsAsync(ConcertStatus? statusFilter, CancellationToken ct);
    Task<ConcertDetailDto?> GetConcertByIdAsync(Guid id, CancellationToken ct);
    Task<ConcertDetailDto> CreateConcertAsync(CreateConcertRequest request, CancellationToken ct);
    Task<ConcertDetailDto> UpdateConcertAsync(Guid id, UpdateConcertRequest request, CancellationToken ct);
    Task<TicketCategoryDto> AddTicketCategoryAsync(Guid concertId, AddTicketCategoryRequest request, CancellationToken ct);
    Task<ConcertDetailDto> PublishConcertAsync(Guid id, CancellationToken ct);
}
