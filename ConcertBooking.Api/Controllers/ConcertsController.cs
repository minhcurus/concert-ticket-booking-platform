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

    /// <summary>List published concerts, paginated.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        // The upper bound isn't just a sanity check: pageSize is capped at 100, and page is what
        // it's multiplied by to compute Skip()'s offset, so this is the bound that keeps
        // (page - 1) * pageSize from overflowing a signed 32-bit int into a negative OFFSET.
        if (page < 1 || page > 1_000_000)
            return BadRequest(new { error = "page must be between 1 and 1,000,000" });
        if (pageSize < 1 || pageSize > 100)
            return BadRequest(new { error = "pageSize must be between 1 and 100" });

        return Ok(await _catalogService.GetPublishedConcertsAsync(page, pageSize, ct));
    }

    /// <summary>
    /// Get a published concert's detail, including ticket categories, prices, and live
    /// availability.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var concert = await _catalogService.GetPublishedConcertByIdAsync(id, ct);
        return concert is null ? NotFound() : Ok(concert);
    }
}
