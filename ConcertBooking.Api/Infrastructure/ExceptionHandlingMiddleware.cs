using ConcertBooking.Api.Services;

namespace ConcertBooking.Api.Infrastructure;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    public ExceptionHandlingMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
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
                _ => (StatusCodes.Status500InternalServerError, "INTERNAL_ERROR"),
            };
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error });
        }
    }
}
