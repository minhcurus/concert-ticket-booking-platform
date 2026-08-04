using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace ConcertBooking.Api.Controllers;

[ApiController]
[Route("api/ping")]
public class PingController : ControllerBase
{
    [HttpGet("customer")]
    [RequireRole(UserRole.Customer)]
    public IActionResult Customer() => Ok(new { ok = true });

    [HttpGet("operator")]
    [RequireRole(UserRole.Operator)]
    public IActionResult Operator() => Ok(new { ok = true });
}
