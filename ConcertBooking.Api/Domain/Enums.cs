namespace ConcertBooking.Api.Domain;

public enum UserRole { Customer, Operator }
public enum ConcertStatus { Draft, Published, Cancelled }
public enum BookingStatus { PendingPayment, Confirmed, Cancelled, Expired }
public enum VoucherDiscountType { Percentage, FixedAmount }
