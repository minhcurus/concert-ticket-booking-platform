using ConcertBooking.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ConcertBooking.Api.Infrastructure;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException)
        {
            // The client disconnected or the request was cancelled — there is no response to
            // write, and this is routine under load, not a server error.
        }
        catch (Exception ex)
        {
            var (status, error) = ex switch
            {
                SoldOutException => (StatusCodes.Status409Conflict, "SOLD_OUT"),
                InvalidVoucherException e => (StatusCodes.Status422UnprocessableEntity, e.Message),
                TicketCategoryNotFoundException => (StatusCodes.Status404NotFound, "TICKET_CATEGORY_NOT_FOUND"),
                BookingNotFoundException => (StatusCodes.Status404NotFound, "BOOKING_NOT_FOUND"),
                ConcertNotFoundException => (StatusCodes.Status404NotFound, "CONCERT_NOT_FOUND"),
                InvalidBookingStateException e => (StatusCodes.Status409Conflict, e.Message),
                InvalidBookingRequestException e => (StatusCodes.Status400BadRequest, e.Message),
                InvalidConcertRequestException e => (StatusCodes.Status400BadRequest, e.Message),
                ConcertNotPublishableException e => (StatusCodes.Status409Conflict, e.Message),
                DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } }
                    => (StatusCodes.Status409Conflict, "CONFLICT"),
                _ => (StatusCodes.Status500InternalServerError, "INTERNAL_ERROR"),
            };

            if (status == StatusCodes.Status500InternalServerError)
                _logger.LogError(ex, "Unhandled exception processing {Method} {Path}", context.Request.Method, context.Request.Path);
            else
                _logger.LogWarning(ex, "{Error} handling {Method} {Path}", error, context.Request.Method, context.Request.Path);

            if (context.Response.HasStarted) return;

            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error });
        }
    }
}
