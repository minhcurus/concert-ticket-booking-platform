using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using ConcertBooking.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ConcertBooking.Api.Controllers;

[ApiController]
[Route("api/bookings")]
[RequireRole(UserRole.Customer)]
public class BookingsController : ControllerBase
{
    private readonly IBookingService _bookingService;
    private readonly CurrentUserAccessor _currentUser;

    public BookingsController(IBookingService bookingService, CurrentUserAccessor currentUser)
    {
        _bookingService = bookingService;
        _currentUser = currentUser;
    }

    [HttpPost]
    [EnableRateLimiting("booking")]
    public async Task<ActionResult<BookingDto>> Create(
        [FromBody] CreateBookingRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return BadRequest(new { error = "Idempotency-Key header is required" });

        var result = await _bookingService.CreateBookingAsync(_currentUser.UserId!.Value, request, idempotencyKey, ct);
        return result.WasIdempotentReplay
            ? Ok(result.Booking)
            : CreatedAtAction(nameof(GetById), new { id = result.Booking.Id }, result.Booking);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BookingDto>>> GetMine(CancellationToken ct)
        => Ok(await _bookingService.GetUserBookingsAsync(_currentUser.UserId!.Value, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<BookingDto>> GetById(Guid id, CancellationToken ct)
    {
        var booking = await _bookingService.GetBookingByIdAsync(_currentUser.UserId!.Value, id, ct);
        return booking is null ? NotFound() : Ok(booking);
    }

    [HttpPost("{id:guid}/confirm-payment")]
    public async Task<ActionResult<BookingDto>> ConfirmPayment(Guid id, CancellationToken ct)
        => Ok(await _bookingService.ConfirmPaymentAsync(_currentUser.UserId!.Value, id, ct));

    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<BookingDto>> Cancel(Guid id, CancellationToken ct)
        => Ok(await _bookingService.CancelBookingAsync(_currentUser.UserId!.Value, id, ct));
}
