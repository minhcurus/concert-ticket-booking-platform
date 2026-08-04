using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Infrastructure;
using ConcertBooking.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace ConcertBooking.Api.Controllers;

[ApiController]
[Route("api/concerts")]
[RequireRole(UserRole.Customer)]
public class ConcertsController : ControllerBase
{
    private readonly ICatalogService _catalogService;
    public ConcertsController(ICatalogService catalogService) => _catalogService = catalogService;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
        => Ok(await _catalogService.GetPublishedConcertsAsync(page, pageSize, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var concert = await _catalogService.GetPublishedConcertByIdAsync(id, ct);
        return concert is null ? NotFound() : Ok(concert);
    }
}
