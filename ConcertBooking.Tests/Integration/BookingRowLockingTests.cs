using System.Net;
using System.Net.Http.Json;
using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using ConcertBooking.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ConcertBooking.Tests.Integration;

/// <summary>
/// Proves that ConfirmPaymentAsync/CancelBookingAsync/ExpireStaleHoldsAsync take a real
/// SELECT ... FOR UPDATE row lock on the Booking itself, as the first thing inside their
/// transaction, before Status is read or mutated. Without this lock, a concurrent
/// CancelBookingAsync and stale-hold sweep could both observe Status == PendingPayment before
/// either commits, and both release the same booking's inventory — double-crediting
/// AvailableQuantity. The DB-level row lock is what makes such interleavings impossible: the
/// second transaction's attempt to lock the same Booking row must block until the first
/// transaction commits or rolls back.
///
/// This test holds the row lock manually from one connection/transaction, then asserts that a
/// concurrent BookingService call touching the same booking row is blocked for as long as the
/// lock is held, and only completes once it's released. This is deterministic (not timing-
/// sensitive in the way a true multi-request race would be), because Postgres row-level locking
/// guarantees the blocking behavior regardless of scheduling.
/// </summary>
[Collection("Api")]
public class BookingRowLockingTests
{
    private readonly ApiFactory _factory;
    public BookingRowLockingTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ConfirmPayment_BlocksWhileAnotherTransactionHoldsTheBookingRowLock()
    {
        // Arrange: create a real booking via the API.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", DbSeeder.CustomerId.ToString());
        client.DefaultRequestHeaders.Add("Idempotency-Key", $"row-lock-{Guid.NewGuid()}");
        var createRequest = new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, null);
        var createResponse = await client.PostAsJsonAsync("/api/bookings", createRequest);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var booking = await createResponse.Content.ReadFromJsonAsync<BookingDto>();

        // Act: manually take the same FOR UPDATE lock BookingService takes internally, on a
        // separate connection/transaction, and hold it open.
        using var lockScope = _factory.Services.CreateScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var externalTransaction = await lockDb.Database.BeginTransactionAsync();
        await lockDb.Bookings
            .FromSqlInterpolated($"SELECT * FROM \"Bookings\" WHERE \"Id\" = {booking!.Id} FOR UPDATE")
            .SingleAsync();

        // A concurrent ConfirmPaymentAsync call (separate scope -> separate DbContext/connection)
        // needs the same row lock and must block while externalTransaction holds it.
        using var confirmScope = _factory.Services.CreateScope();
        var bookingService = confirmScope.ServiceProvider.GetRequiredService<IBookingService>();
        var confirmTask = bookingService.ConfirmPaymentAsync(DbSeeder.CustomerId, booking.Id, CancellationToken.None);

        var wonRace = await Task.WhenAny(confirmTask, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.NotSame(confirmTask, wonRace); // still blocked: the lock is genuinely held

        // Assert: releasing the external lock lets ConfirmPaymentAsync proceed and complete.
        await externalTransaction.RollbackAsync();
        var confirmed = await confirmTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Confirmed", confirmed.Status);
    }

    [Fact]
    public async Task CancelThenExpireSweep_OnSameBooking_NeverDoubleCreditsInventory()
    {
        // Regression test for the double-release race: create a booking, then race
        // CancelBookingAsync against ExpireStaleHoldsAsync for the very same stale booking.
        // Because both now lock the Booking row first, whichever wins the race transitions the
        // booking to a terminal state (Cancelled or Expired) and releases inventory exactly
        // once; the loser's status recheck (under its own lock) finds a non-PendingPayment
        // booking and does nothing further.
        using var setupScope = _factory.Services.CreateScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var categoryBefore = await setupDb.TicketCategories.AsNoTracking().SingleAsync(c => c.Id == DbSeeder.StandardCategoryId);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", DbSeeder.CustomerId.ToString());
        client.DefaultRequestHeaders.Add("Idempotency-Key", $"race-{Guid.NewGuid()}");
        var createRequest = new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 3) }, null);
        var createResponse = await client.PostAsJsonAsync("/api/bookings", createRequest);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var booking = await createResponse.Content.ReadFromJsonAsync<BookingDto>();

        // Backdate the hold so the sweep also considers this booking stale.
        using (var backdateScope = _factory.Services.CreateScope())
        {
            var backdateDb = backdateScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var trackedBooking = await backdateDb.Bookings.SingleAsync(b => b.Id == booking!.Id);
            trackedBooking.HoldExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await backdateDb.SaveChangesAsync();
        }

        using var cancelScope = _factory.Services.CreateScope();
        var cancelService = cancelScope.ServiceProvider.GetRequiredService<IBookingService>();
        using var sweepScope = _factory.Services.CreateScope();
        var sweepService = sweepScope.ServiceProvider.GetRequiredService<IBookingService>();

        var cancelTask = SafeCancel(cancelService, booking!.Id);
        var sweepTask = sweepService.ExpireStaleHoldsAsync(CancellationToken.None);
        await Task.WhenAll(cancelTask, sweepTask);

        using var assertScope = _factory.Services.CreateScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var categoryAfter = await assertDb.TicketCategories.AsNoTracking().SingleAsync(c => c.Id == DbSeeder.StandardCategoryId);
        var finalBooking = await assertDb.Bookings.AsNoTracking().SingleAsync(b => b.Id == booking.Id);

        // Exactly one of the two terminal states, and inventory credited exactly once (+3, not +6).
        Assert.True(finalBooking.Status is BookingStatus.Cancelled or BookingStatus.Expired);
        Assert.Equal(categoryBefore.AvailableQuantity, categoryAfter.AvailableQuantity);
    }

    private static async Task SafeCancel(IBookingService service, Guid bookingId)
    {
        try
        {
            await service.CancelBookingAsync(DbSeeder.CustomerId, bookingId, CancellationToken.None);
        }
        catch (InvalidBookingStateException)
        {
            // Expected if the sweep won the race and already transitioned the booking away
            // from PendingPayment before CancelBookingAsync acquired the row lock.
        }
    }
}
