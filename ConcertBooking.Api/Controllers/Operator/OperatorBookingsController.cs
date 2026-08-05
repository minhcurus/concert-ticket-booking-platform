using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using ConcertBooking.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace ConcertBooking.Api.Controllers.Operator;

[ApiController]
[Route("api/operator/bookings")]
[RequireRole(UserRole.Operator)]
public class OperatorBookingsController : ControllerBase
{
    private readonly IOperatorBookingService _operatorBookingService;
    public OperatorBookingsController(IOperatorBookingService operatorBookingService) => _operatorBookingService = operatorBookingService;

    /// <summary>
    /// Monitor all bookings across all customers, filterable by status, concert, and
    /// flagged state.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] BookingStatus? status, [FromQuery] Guid? concertId, [FromQuery] bool? isFlagged, CancellationToken ct)
        => Ok(await _operatorBookingService.GetBookingsAsync(status, concertId, isFlagged, ct));

    /// <summary>
    /// Manually override a booking's status, bypassing the normal customer-facing state
    /// machine (e.g. force-confirm or force-cancel).
    /// </summary>
    [HttpPut("{id:guid}/status")]
    public async Task<IActionResult> OverrideStatus(Guid id, [FromBody] UpdateBookingStatusRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<BookingStatus>(request.Status, ignoreCase: true, out var newStatus))
            return BadRequest(new { error = $"Unknown status '{request.Status}'" });

        return Ok(await _operatorBookingService.OverrideStatusAsync(id, newStatus, ct));
    }

    /// <summary>Mark a booking as suspicious, with a reason, for later review.</summary>
    [HttpPost("{id:guid}/flag")]
    public async Task<IActionResult> Flag(Guid id, [FromBody] FlagBookingRequest request, CancellationToken ct)
        => Ok(await _operatorBookingService.FlagBookingAsync(id, request.Reason, ct));
}
