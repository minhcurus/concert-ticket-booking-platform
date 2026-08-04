using ConcertBooking.Api.Domain;

namespace ConcertBooking.Api.Infrastructure;

public class CurrentUserAccessor
{
    public Guid? UserId { get; set; }
    public UserRole? Role { get; set; }
}
