using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using ConcertBooking.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace ConcertBooking.Api.Controllers.Operator;

[ApiController]
[Route("api/operator/concerts")]
[RequireRole(UserRole.Operator)]
public class OperatorConcertsController : ControllerBase
{
    private readonly IOperatorConcertService _operatorConcertService;
    public OperatorConcertsController(IOperatorConcertService operatorConcertService) => _operatorConcertService = operatorConcertService;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ConcertStatus? status, CancellationToken ct)
        => Ok(await _operatorConcertService.GetAllConcertsAsync(status, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var concert = await _operatorConcertService.GetConcertByIdAsync(id, ct);
        return concert is null ? NotFound() : Ok(concert);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateConcertRequest request, CancellationToken ct)
    {
        var concert = await _operatorConcertService.CreateConcertAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = concert.Id }, concert);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateConcertRequest request, CancellationToken ct)
        => Ok(await _operatorConcertService.UpdateConcertAsync(id, request, ct));

    [HttpPost("{id:guid}/ticket-categories")]
    public async Task<IActionResult> AddTicketCategory(Guid id, [FromBody] AddTicketCategoryRequest request, CancellationToken ct)
        => Ok(await _operatorConcertService.AddTicketCategoryAsync(id, request, ct));

    [HttpPost("{id:guid}/publish")]
    public async Task<IActionResult> Publish(Guid id, CancellationToken ct)
        => Ok(await _operatorConcertService.PublishConcertAsync(id, ct));
}
