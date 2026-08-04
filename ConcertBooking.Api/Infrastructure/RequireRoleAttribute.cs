using ConcertBooking.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ConcertBooking.Api.Infrastructure;

public class RequireRoleAttribute : Attribute, IAsyncActionFilter
{
    private readonly UserRole _role;
    public RequireRoleAttribute(UserRole role) => _role = role;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var accessor = context.HttpContext.RequestServices.GetRequiredService<CurrentUserAccessor>();
        if (accessor.Role != _role)
        {
            context.Result = new ObjectResult(new { error = $"Requires {_role} role" }) { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }
        await next();
    }
}
