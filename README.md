# Concert Ticket Booking Platform

A simplified backend for an online concert ticket booking platform: customer booking flows (browse → reserve → pay/cancel, with vouchers)
and an internal operator dashboard (publish concerts, monitor bookings, manual override), built
around correctness under concurrent load — no overselling, no duplicate bookings, no voucher
over-redemption.

- **Design & concurrency reasoning**: [`docs/architecture.md`](docs/architecture.md)
- **Assumptions / what's done / what's not**: [`docs/ASSUMPTIONS.md`](docs/ASSUMPTIONS.md)
- **API testing collection**: [`postman/`](postman/) (see [API Testing Collection](#api-testing-collection-postman) below)

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://www.docker.com/) with Compose (Docker Desktop, or Docker Engine + the
  `docker compose` plugin)

No other local install is required — PostgreSQL and Redis run in containers.

## Local Setup

```bash
# 1. Start PostgreSQL + Redis
docker compose up -d

# 2. Run the API (auto-applies EF Core migrations and seeds demo data on first run —
#    see "Database & Migrations" below)
dotnet run --project ConcertBooking.Api

# 3. Verify it's up
curl http://localhost:5018/health
# -> {"status":"healthy"}
```

The API listens on `http://localhost:5018` (HTTP) / `https://localhost:7221` (HTTPS) by default —
see `ConcertBooking.Api/Properties/launchSettings.json`. All examples below use the HTTP port.

To stop and remove the containers: `docker compose down` (add `-v` to also drop the Postgres
volume and start from a clean database next time).

## Database & Migrations

The app auto-applies pending EF Core migrations and seeds deterministic demo data
(`ConcertBooking.Api/Infrastructure/DbSeeder.cs`) on startup, **but only when
`ASPNETCORE_ENVIRONMENT=Development`** (the default for `dotnet run` via `launchSettings.json`) —
see `ConcertBooking.Api/Program.cs`. No manual migration step is needed for local development.

If you add a new migration and want to inspect/apply it manually instead of relying on
auto-migrate:

```bash
dotnet tool install --global dotnet-ef   # once, if not already installed
dotnet ef migrations add <Name> --project ConcertBooking.Api
dotnet ef database update --project ConcertBooking.Api
```

Connection strings are in `ConcertBooking.Api/appsettings.Development.json` and match
`docker-compose.yml`'s `postgres`/`redis` services out of the box.

## Running Tests

```bash
dotnet test                                                    # full suite (unit + integration)
dotnet test --filter "FullyQualifiedName~<TestName>"           # a single test / class
```

Integration tests use [Testcontainers](https://testcontainers.com/) to spin up real, disposable
PostgreSQL and Redis containers per test run — **Docker must be running** for `dotnet test` to
pass, but you don't need `docker compose up` first; Testcontainers manages its own containers
independently of `docker-compose.yml`.

## API Documentation (Swagger)

With the API running in Development, Swagger UI is available at:

```
http://localhost:5018/swagger
```

## Seeded Users

Auth is a header-based stand-in (`X-User-Id` resolved against a seeded `Users` table — no
passwords/JWT; see [`docs/ASSUMPTIONS.md`](docs/ASSUMPTIONS.md) for why). Three users are
seeded on startup:

| Name | `X-User-Id` | Role |
|---|---|---|
| Alice Customer | `11111111-1111-1111-1111-111111111111` | `Customer` |
| Carol Customer | `77777777-7777-7777-7777-777777777777` | `Customer` |
| Bob Operator | `22222222-2222-2222-2222-222222222222` | `Operator` |

A concert ("Flash Sale Live", already `Published`), two ticket categories (VIP $250 / Standard
$90), and a voucher (`LAUNCH10`, 10% off) are also seeded — exact IDs in
`ConcertBooking.Api/Infrastructure/DbSeeder.cs`, and pre-filled in the
[Postman environment](postman/ConcertBooking.postman_environment.json).

## API Overview

Full endpoint list with request/response shapes is in
[`docs/ASSUMPTIONS.md#whats-done`](docs/ASSUMPTIONS.md#whats-done). The most load-bearing ones,
with working `curl` examples against the seeded data above:

**Browse the catalog** (no auth restrictions beyond `Customer` role):

```bash
curl http://localhost:5018/api/concerts \
  -H "X-User-Id: 11111111-1111-1111-1111-111111111111"
```

**Reserve tickets** — `Idempotency-Key` is required; re-sending the same key returns the same
booking instead of creating a duplicate:

```bash
curl -X POST http://localhost:5018/api/bookings \
  -H "X-User-Id: 11111111-1111-1111-1111-111111111111" \
  -H "Idempotency-Key: $(uuidgen)" \
  -H "Content-Type: application/json" \
  -d '{
    "items": [{ "ticketCategoryId": "55555555-5555-5555-5555-555555555555", "quantity": 2 }],
    "voucherCode": null
  }'
```

**Confirm payment** (simulated) — replace `<bookingId>` with the `id` from the response above:

```bash
curl -X POST http://localhost:5018/api/bookings/<bookingId>/confirm-payment \
  -H "X-User-Id: 11111111-1111-1111-1111-111111111111"
```

**Operator: publish a concert** (`X-User-Id` must resolve to `Operator`, else `403`):

```bash
curl -X POST http://localhost:5018/api/operator/concerts \
  -H "X-User-Id: 22222222-2222-2222-2222-222222222222" \
  -H "Content-Type: application/json" \
  -d '{
    "title": "New Show",
    "description": "Demo",
    "venueName": "Arena",
    "startTime": "2027-01-01T20:00:00Z"
  }'
```

**Operator: force-override a booking's status** (bypasses the normal customer state machine):

```bash
curl -X PUT http://localhost:5018/api/operator/bookings/<bookingId>/status \
  -H "X-User-Id: 22222222-2222-2222-2222-222222222222" \
  -H "Content-Type: application/json" \
  -d '{ "status": "Cancelled" }'
```

## API Testing Collection (Postman)

- Collection: [`postman/ConcertBooking.postman_collection.json`](postman/ConcertBooking.postman_collection.json)
- Environment: [`postman/ConcertBooking.postman_environment.json`](postman/ConcertBooking.postman_environment.json)

Import both into Postman, select the **"Concert Booking - Local"** environment, then run the
**"1. Customer Flow"** folder top-to-bottom followed by **"2. Operator Flow"** (some requests
capture an ID — e.g. `bookingId` — into an environment variable that a later request in the same
folder depends on). Every request has a test script asserting status code and key response
fields, and both folders include at least one negative-path check (cancel-after-confirm → `409`,
customer hitting an operator route → `403`). No manual editing needed — `baseUrl` and every
`X-User-Id` are pre-filled with the seeded data above.

## Coding Guidelines

**Project layout** (folder-modularized, single project — see
[`docs/architecture.md`](docs/architecture.md#solution-structure) for the rationale):

```
ConcertBooking.Api/
  Controllers/        thin controllers — no business logic, just request/response mapping
  Controllers/Operator/  operator-only controllers ([RequireRole(UserRole.Operator)])
  Services/            business logic + transaction boundaries; one interface per service
  Domain/              entities, enums
  Infrastructure/       DbContext, Redis client, middleware, BackgroundService(s)
  Dtos/                 request/response records
```

**Adding a new endpoint:**

1. Add the request/response DTO(s) as `record`s in `Dtos/`.
2. Add the method to the relevant service interface (`I*Service.cs`) and implement it in the
   service class — this is where transactions, locking, and validation live. If the endpoint
   touches `Booking`/`TicketCategory`/`Voucher` state, route inventory/voucher mutations through
   `Services/BookingConcurrencyGuard.cs` rather than writing new lock/release logic — that's the
   single place both the customer and operator write paths share so they can't silently diverge.
3. Add the controller action. Keep it a one-line call into the service; role-gate with
   `[RequireRole(UserRole.Customer)]` / `[RequireRole(UserRole.Operator)]` at the controller
   level unless the route needs mixed access.
4. If the service can fail in a way the caller should see as a specific HTTP status (not a
   generic 500), add an exception type and wire it into
   `Infrastructure/ExceptionHandlingMiddleware.cs`'s `switch` — see the naming convention below.
5. Write an integration test in `ConcertBooking.Tests/Integration/` against the real
   Testcontainers-backed Postgres/Redis (never EF Core's InMemory provider for anything
   touching concurrency — it can't exercise real transaction/locking semantics).

**Exception → HTTP status convention** (`Infrastructure/ExceptionHandlingMiddleware.cs`):

| Exception type | Status | When to use it |
|---|---|---|
| `*NotFoundException` (`BookingNotFoundException`, `ConcertNotFoundException`, `TicketCategoryNotFoundException`) | `404` | The referenced entity doesn't exist (or isn't visible to this caller). |
| `InvalidBookingRequestException`, `InvalidConcertRequestException` | `400` | The request body itself is invalid (e.g. negative quantity, past start date) — a retry with a different body could succeed. |
| `SoldOutException`, `InvalidBookingStateException`, `ConcertNotPublishableException` | `409` | The request is well-formed but conflicts with current state (out of stock, wrong booking status, no ticket categories yet). |
| `InvalidVoucherException` | `422` | The request and target state are both fine, but the voucher itself isn't usable (expired, exhausted, already redeemed by this user). |
| *(unmapped exception)* | `500` | Falls through to the generic handler and gets logged at `Error` — if you're adding a new failure mode that a caller should react to differently, map it explicitly rather than leaving it to fall through. |

Follow this pattern (a small `Exception` subclass with a message, mapped in the middleware
`switch`) rather than throwing generic exceptions or returning ad-hoc `BadRequest(...)` objects
from inside a service.

**Running a single unit test** while iterating:

```bash
dotnet test --filter "FullyQualifiedName~BookingCreationTests.Create_WithValidVoucher_AppliesDiscountAndRecordsRedemption"
```
