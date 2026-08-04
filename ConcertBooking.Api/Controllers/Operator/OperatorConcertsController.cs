using ConcertBooking.Api.Domain;
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
}
