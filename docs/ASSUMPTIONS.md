# Assumptions, Scope & Limitations

This document exists so a reviewer can see, in one place, what was assumed, what was built, and
what was deliberately left out — without reading the full design doc. For the technical
reasoning behind each decision (why Postgres over Redis for correctness, why `SELECT ... FOR
UPDATE` over a distributed lock, etc.), see [`docs/architecture.md`](architecture.md).

## Assumptions

Assumptions made where the requirements didn't specify an exact behaviour:

- **Traffic shape**: the assumed flash-sale scenario (~50,000 users, peak 300–500 booking
  requests/minute) works out to ~5–8 requests/second sustained peak. The concurrency design is
  sized for that order of magnitude, not for a general high-throughput ticketing platform (see
  "Booking flow & concurrency design" in `architecture.md` for the three approaches considered
  at that scale).
- **Single API instance in the reference deployment.** The hold-expiry background sweep is
  coordinated by a Redis leader-lock specifically so it *would* be safe to run more than one
  instance, but nothing else in this project (e.g. the rate limiter) was built with multiple
  instances in mind — see "Rate limiting is in-memory/per-instance" below.
- **Operators are trusted, internal users.** The operator override endpoint
  (`PUT /api/operator/bookings/{id}/status`) is an intentional escape hatch that bypasses the
  normal booking state machine. It assumes whoever holds an `Operator`-role `X-User-Id` is
  authorized to force any status transition — there is no secondary approval step or audit log
  beyond what's already stored on the booking (`IsFlagged`/`FlagReason`).
- **A booking's items all belong to one concert, and that concert must be `Published`** at
  booking time. The alternative (mixed-concert
  baskets) has no sensible `Booking.ConcertId` to assign and complicates the voucher's
  concert-scoping check, so it's enforced as a hard constraint (`400 Bad Request` otherwise).
- **Vouchers are pre-provisioned data**, not something a customer or operator creates through the
  API — a deliberate scope cut ("No voucher CRUD APIs" below).
- **"Confirm payment" is a stand-in for a real payment callback.** Payment is
  *simulated*, so `POST /api/bookings/{id}/confirm-payment` unconditionally
  succeeds for a booking that's still `PendingPayment` and not past its hold — there's no
  simulated failure/decline path, since the booking flow doesn't need to react to a
  failed payment differently from an abandoned one (both just expire).
- **It runs locally**, not as a deployment — so environment configuration
  (`appsettings.Development.json`, `docker-compose.yml`) targets a local Docker setup, not a
  cloud target.

## What's Done

All of the following is implemented and covered by the integration/unit test suite
(`dotnet test` — 69 tests, all passing as of this writing).

**Customer-facing** (`X-User-Id` must resolve to a seeded `Customer`):

| Method & Path | Purpose |
|---|---|
| `GET /api/concerts` | List published concerts, paginated (`page`, `pageSize`, validated). |
| `GET /api/concerts/{id}` | Concert detail incl. ticket categories, price, live availability. |
| `POST /api/bookings` | Reserve tickets (+ optional voucher code); requires `Idempotency-Key`. |
| `GET /api/bookings` | Current user's own bookings. |
| `GET /api/bookings/{id}` | A single booking of the current user's. |
| `POST /api/bookings/{id}/confirm-payment` | Simulated payment success: `PendingPayment → Confirmed`. |
| `POST /api/bookings/{id}/cancel` | Cancel own `PendingPayment` booking, releases inventory + voucher. |

**Operator-facing** (`X-User-Id` must resolve to a seeded `Operator`, else `403`):

| Method & Path | Purpose |
|---|---|
| `GET /api/operator/concerts` | List all concerts regardless of status, filterable by status. |
| `GET /api/operator/concerts/{id}` | Concert detail for any status. |
| `POST /api/operator/concerts` | Create a concert (starts as `Draft`). |
| `PUT /api/operator/concerts/{id}` | Update concert details. |
| `POST /api/operator/concerts/{id}/ticket-categories` | Add a ticket category to a concert. |
| `POST /api/operator/concerts/{id}/publish` | `Draft → Published` (requires ≥1 ticket category). |
| `GET /api/operator/bookings` | Monitor all bookings, filterable by status/concert/`isFlagged`. |
| `PUT /api/operator/bookings/{id}/status` | Manual status override (bypasses the normal state machine). |
| `POST /api/operator/bookings/{id}/flag` | Mark a booking as suspicious, with a reason. |

**Correctness guarantees, proven by the flagship concurrency test (`BookingConcurrencyTests.cs`)
and enforced by both application-level locking and independent DB constraints:**

- **No overselling**: `SELECT ... FOR UPDATE` on every referenced `TicketCategory`, sorted by
  `Id` to avoid deadlocks, plus a `CHECK (AvailableQuantity >= 0)` DB constraint as a second line
  of defense.
- **No duplicate bookings**: a required `Idempotency-Key` header, backed by a unique DB index —
  a retried request with the same key always returns the original booking, not a new one.
- **No voucher over-redemption**: a unique `(VoucherId, UserId)` constraint plus a row-locked
  `RedeemedCount < MaxRedemptions` check, shared by every code path that can create a voucher
  redemption (booking creation *and* the operator's reinstate-a-cancelled-booking path) so the
  two can't silently drift apart.
- **System stability under load**: Redis (idempotency fast-path, catalog cache, rate-limit
  partition, sweep leader-lock) is a performance/coordination layer only — every Redis call
  degrades to querying Postgres directly on failure or timeout, so a Redis outage never takes
  the API down.

**Testing**: xUnit unit tests for pure logic (voucher discount calculation), and Testcontainers
integration tests against real PostgreSQL + Redis containers for everything concurrency-related
(EF Core's InMemory provider is never used for those, since it can't exercise real
transaction/locking semantics).

## What's Not Done (Limitations & Trade-offs)

Explicitly out of scope, each an accepted cut given the project's scope and focus
(the booking-concurrency core, not feature breadth):

- **No real authentication** — no passwords, no JWT. Identity is a seeded-user, header-based
  stand-in (`X-User-Id` resolved against a seeded `Users` table). This is deliberate: the
  focus is booking-domain design, not auth infrastructure.
- **No real payment gateway** — a single endpoint simulates a successful payment callback; there
  is no decline/retry path.
- **No voucher CRUD APIs** — vouchers are seed data only, to keep effort on the booking flow.
- **No refunds** after a booking reaches `Confirmed`.
- **No seat-level or assigned seating** — inventory is tracked as category + quantity only, not
  individual seats.
- **No notifications/email** on any booking state change.
- **Rate limiting is in-memory/per-instance**, not distributed. A real multi-instance deployment
  would need a Redis-backed limiter; documented here rather than built, since it would add
  complexity without being exercisable in a single-instance local setup.
- **No pagination cursoring** — simple offset/limit (`page`/`pageSize`) on the catalog endpoint.
- **Operators cannot delete concerts or ticket categories** (create/update/publish only) —
  deleting live inventory that customers may already hold bookings against is a business decision
  this system doesn't attempt to adjudicate.
- **No audit trail on the operator override endpoint** beyond what's already on the booking row
  (`IsFlagged`/`FlagReason`) — there's no separate log of who overrode what status, when.
- **No API versioning** — a single, unversioned `api/` surface, appropriate for a project of
  this scope but a genuine gap if this were to evolve into a real service with external
  consumers.
