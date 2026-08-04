using Microsoft.EntityFrameworkCore;

namespace ConcertBooking.Api.Infrastructure;

public class CurrentUserMiddleware
{
    private readonly RequestDelegate _next;
    public CurrentUserMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, AppDbContext db, CurrentUserAccessor accessor)
    {
        if (context.Request.Path.StartsWithSegments("/health") || context.Request.Path.StartsWithSegments("/swagger"))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-User-Id", out var headerValue) ||
            !Guid.TryParse(headerValue, out var userId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid X-User-Id header" });
            return;
        }

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Unknown user" });
            return;
        }

        accessor.UserId = user.Id;
        accessor.Role = user.Role;
        await _next(context);
    }
}
