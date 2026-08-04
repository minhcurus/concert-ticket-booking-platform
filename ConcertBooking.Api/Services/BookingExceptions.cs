namespace ConcertBooking.Api.Services;

public class TicketCategoryNotFoundException : Exception
{
    public TicketCategoryNotFoundException(Guid id) : base($"Ticket category {id} not found") { }
}

public class SoldOutException : Exception
{
    public SoldOutException(Guid ticketCategoryId) : base($"Ticket category {ticketCategoryId} is sold out") { }
}

public class InvalidVoucherException : Exception
{
    public InvalidVoucherException(string message) : base(message) { }
}

public class BookingNotFoundException : Exception
{
    public BookingNotFoundException(Guid id) : base($"Booking {id} not found") { }
}

public class InvalidBookingStateException : Exception
{
    public InvalidBookingStateException(string message) : base(message) { }
}
