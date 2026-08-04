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
    {
        if (page < 1)
            return BadRequest(new { error = "page must be at least 1" });
        if (pageSize < 1 || pageSize > 100)
            return BadRequest(new { error = "pageSize must be between 1 and 100" });

        return Ok(await _catalogService.GetPublishedConcertsAsync(page, pageSize, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var concert = await _catalogService.GetPublishedConcertByIdAsync(id, ct);
        return concert is null ? NotFound() : Ok(concert);
    }
}
