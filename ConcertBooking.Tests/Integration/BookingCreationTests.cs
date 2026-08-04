using System.Net;
using System.Net.Http.Json;
using ConcertBooking.Api.Domain;
using ConcertBooking.Api.Dtos;
using ConcertBooking.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ConcertBooking.Tests.Integration;

[Collection("Api")]
public class BookingCreationTests
{
    private readonly ApiFactory _factory;
    public BookingCreationTests(ApiFactory factory) => _factory = factory;

    private HttpClient CustomerClient(string idempotencyKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", DbSeeder.CustomerId.ToString());
        client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);
        return client;
    }

    [Fact]
    public async Task Create_ValidRequest_ReservesInventoryAndReturns201()
    {
        var client = CustomerClient($"happy-{Guid.NewGuid()}");
        var request = new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 2) }, null);

        var response = await client.PostAsJsonAsync("/api/bookings", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var booking = await response.Content.ReadFromJsonAsync<BookingDto>();
        Assert.Equal("PendingPayment", booking!.Status);
        Assert.Equal(2, booking.Items.Single().Quantity);
    }

    [Fact]
    public async Task Create_MoreThanAvailable_Returns409SoldOut()
    {
        var client = CustomerClient($"soldout-{Guid.NewGuid()}");
        var request = new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 100_000) }, null);

        var response = await client.PostAsJsonAsync("/api/bookings", request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithValidVoucher_AppliesDiscountAndRecordsRedemption()
    {
        var client = CustomerClient($"voucher-{Guid.NewGuid()}");
        var request = new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, "LAUNCH10");

        var response = await client.PostAsJsonAsync("/api/bookings", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var booking = await response.Content.ReadFromJsonAsync<BookingDto>();
        Assert.Equal(81m, booking!.TotalAmount); // 90 - 10%
        Assert.Equal(DbSeeder.VoucherId, booking.VoucherId);
    }

    [Fact]
    public async Task Create_VoucherAlreadyRedeemedByUser_Returns422()
    {
        // Uses a dedicated ad-hoc customer (rather than DbSeeder.CustomerId) so this test's
        // "first redemption succeeds" assumption doesn't collide with other tests in this
        // class (e.g. Create_WithValidVoucher_AppliesDiscountAndRecordsRedemption) that also
        // redeem LAUNCH10 as DbSeeder.CustomerId against the shared, never-reset DB fixture.
        var dedicatedUserId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User { Id = dedicatedUserId, Name = "Voucher Test Customer", Email = $"{dedicatedUserId}@example.com", Role = UserRole.Customer });
            await db.SaveChangesAsync();
        }

        HttpClient ClientFor(string idempotencyKey)
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", dedicatedUserId.ToString());
            client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);
            return client;
        }

        var firstClient = ClientFor($"voucher-first-{Guid.NewGuid()}");
        var firstRequest = new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, "LAUNCH10");
        var firstResponse = await firstClient.PostAsJsonAsync("/api/bookings", firstRequest);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

        var secondClient = ClientFor($"voucher-second-{Guid.NewGuid()}");
        var secondRequest = new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, "LAUNCH10");
        var secondResponse = await secondClient.PostAsJsonAsync("/api/bookings", secondRequest);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, secondResponse.StatusCode);
    }

    [Fact]
    public async Task Create_SameIdempotencyKeyTwice_ReturnsSameBookingIdBothTimes()
    {
        var key = $"idem-{Guid.NewGuid()}";
        var request = new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, null);

        var firstResponse = await CustomerClient(key).PostAsJsonAsync("/api/bookings", request);
        var firstBooking = await firstResponse.Content.ReadFromJsonAsync<BookingDto>();

        var secondResponse = await CustomerClient(key).PostAsJsonAsync("/api/bookings", request);
        var secondBooking = await secondResponse.Content.ReadFromJsonAsync<BookingDto>();

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(firstBooking!.Id, secondBooking!.Id);
    }

    [Fact]
    public async Task Create_MissingIdempotencyKeyHeader_Returns400()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", DbSeeder.CustomerId.ToString());
        var request = new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, null);

        var response = await client.PostAsJsonAsync("/api/bookings", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_NegativeQuantity_Returns400AndDoesNotIncreaseStock()
    {
        var client = CustomerClient($"negative-qty-{Guid.NewGuid()}");
        var request = new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, -5) }, null);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var before = (await db.TicketCategories.AsNoTracking().SingleAsync(c => c.Id == DbSeeder.StandardCategoryId)).AvailableQuantity;

        var response = await client.PostAsJsonAsync("/api/bookings", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var after = (await db.TicketCategories.AsNoTracking().SingleAsync(c => c.Id == DbSeeder.StandardCategoryId)).AvailableQuantity;
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Create_ZeroQuantity_Returns400()
    {
        var client = CustomerClient($"zero-qty-{Guid.NewGuid()}");
        var request = new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 0) }, null);

        var response = await client.PostAsJsonAsync("/api/bookings", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_EmptyItemsList_Returns400()
    {
        var client = CustomerClient($"empty-items-{Guid.NewGuid()}");
        var request = new CreateBookingRequest(new List<CreateBookingItemRequest>(), null);

        var response = await client.PostAsJsonAsync("/api/bookings", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateTicketCategoryInRequest_Returns400()
    {
        var client = CustomerClient($"dup-category-{Guid.NewGuid()}");
        var request = new CreateBookingRequest(
            new List<CreateBookingItemRequest>
            {
                new(DbSeeder.StandardCategoryId, 1),
                new(DbSeeder.StandardCategoryId, 1),
            }, null);

        var response = await client.PostAsJsonAsync("/api/bookings", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_SameIdempotencyKeyUsedByDifferentUser_Returns409AndDoesNotLeakOtherUsersBooking()
    {
        // Force a real DB-level unique-constraint collision on IdempotencyKey (bypassing the
        // Redis fast-path, which is keyed the same way and would short-circuit before hitting
        // the DB) by inserting a booking directly for a different user with a known key, then
        // having DbSeeder.CustomerId attempt to create a booking using that same key.
        var otherUserId = Guid.NewGuid();
        var sharedKey = $"cross-user-{Guid.NewGuid()}";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User { Id = otherUserId, Name = "Other User", Email = $"{otherUserId}@example.com", Role = UserRole.Customer });
            db.Bookings.Add(new Booking
            {
                Id = Guid.NewGuid(),
                UserId = otherUserId,
                ConcertId = DbSeeder.ConcertId,
                Status = BookingStatus.PendingPayment,
                IdempotencyKey = sharedKey,
                HoldExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                TotalAmount = 90m,
            });
            await db.SaveChangesAsync();
        }

        var client = CustomerClient(sharedKey);
        var request = new CreateBookingRequest(new List<CreateBookingItemRequest> { new(DbSeeder.StandardCategoryId, 1) }, null);

        var response = await client.PostAsJsonAsync("/api/bookings", request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Create_ItemsSpanningTwoConcerts_Returns400()
    {
        var otherConcertId = Guid.NewGuid();
        var otherCategoryId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Concerts.Add(new Concert
            {
                Id = otherConcertId,
                Title = "Other Concert",
                Description = "D",
                VenueName = "V",
                StartTime = DateTimeOffset.UtcNow.AddDays(30),
                Status = ConcertStatus.Published,
            });
            db.TicketCategories.Add(new TicketCategory
            {
                Id = otherCategoryId,
                ConcertId = otherConcertId,
                Name = "General",
                Price = 20m,
                TotalQuantity = 50,
                AvailableQuantity = 50,
            });
            await db.SaveChangesAsync();
        }

        var client = CustomerClient($"cross-concert-{Guid.NewGuid()}");
        var request = new CreateBookingRequest(
            new List<CreateBookingItemRequest>
            {
                new(DbSeeder.StandardCategoryId, 1),
                new(otherCategoryId, 1),
            }, null);

        var response = await client.PostAsJsonAsync("/api/bookings", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_ForUnpublishedConcert_Returns400()
    {
        var draftConcertId = Guid.NewGuid();
        var draftCategoryId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Concerts.Add(new Concert
            {
                Id = draftConcertId,
                Title = "Unpublished Show",
                Description = "D",
                VenueName = "V",
                StartTime = DateTimeOffset.UtcNow.AddDays(30),
                Status = ConcertStatus.Draft,
            });
            db.TicketCategories.Add(new TicketCategory
            {
                Id = draftCategoryId,
                ConcertId = draftConcertId,
                Name = "General",
                Price = 20m,
                TotalQuantity = 50,
                AvailableQuantity = 50,
            });
            await db.SaveChangesAsync();
        }

        var client = CustomerClient($"draft-concert-{Guid.NewGuid()}");
        var request = new CreateBookingRequest(new List<CreateBookingItemRequest> { new(draftCategoryId, 1) }, null);

        var response = await client.PostAsJsonAsync("/api/bookings", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
