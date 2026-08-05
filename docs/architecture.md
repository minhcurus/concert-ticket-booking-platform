# Concert Ticket Booking Platform — Design

Date: 2026-08-04
Status: Approved

## Context

This is a personal project to design and implement a simplified backend for an online concert ticket booking
platform, covering:

- Customer-facing booking flows (browse concerts, view ticket categories/prices, reserve
  tickets, apply vouchers, track booking status).
- Internal operator/admin workflows (monitor bookings, publish concerts, manage ticket
  availability, handle failed/suspicious bookings, manually override booking status).

Assumed launch scenario: a flash-sale campaign with ~50,000 users and peak traffic of
300–500 booking requests/minute (~5–8 req/sec sustained peak). The design targets
four risks: overselling tickets, duplicate bookings from client retries,
voucher abuse, and system instability under flash-sale load.

The system is **not** meant to be production-ready; it prioritises
demonstrated reasoning about trade-offs and correctness over feature count. Scope is
deliberately cut in several places below; each cut is called out with its rationale.

## Priority decision

The booking flow — specifically correctness under concurrent load (no overselling, no
duplicate bookings, no voucher over-redemption) — is the centerpiece of this project.
Everything else (browsing, operator dashboard, voucher management) is kept intentionally
minimal so effort concentrates on the part that matters most.

## Tech stack

- **Language/framework**: C# / .NET 10, ASP.NET Core Web API.
- **ORM**: EF Core.
- **Database**: PostgreSQL — chosen for `SELECT ... FOR UPDATE` row-level locking and solid
  transactional guarantees, which the concurrency design below depends on directly.
- **Cache/coordination**: Redis — used only where it earns its keep (see "Booking flow" and
  "Redis usage" below), not as a blanket cache-everything layer.
- **Testing**: xUnit, Testcontainers for .NET (real Postgres + Redis in integration tests).
- **Local orchestration**: `docker-compose.yml` for Postgres + Redis (+ optionally the API).

## Solution structure

Single ASP.NET Core Web API project, folder-modularized — no multi-project "Clean
Architecture" split and no MediatR/CQRS scaffolding. At this scope, that ceremony adds
friction without adding clarity; a reviewer should be able to open one project and read it
top to bottom.

```
ConcertBooking.Api/
  Controllers/
  Services/          business logic, transaction boundaries, one interface per service
  Domain/             entities, enums
  Infrastructure/      EF Core DbContext, Redis client, BackgroundService(s)
  Dtos/
ConcertBooking.Tests/  xUnit unit tests + Testcontainers integration tests
docker-compose.yml
```

Modules (folders, not projects), each a thin controller + a service:

- **Catalog** — concerts, ticket categories, browsing.
- **Booking** — the concurrency-critical core: reservation, hold expiry, payment
  confirmation, cancellation.
- **Voucher** — seeded data + validation/redemption logic invoked *inside* the booking
  transaction. No standalone voucher CRUD API (see [ASSUMPTIONS.md](ASSUMPTIONS.md)).
- **Operator** — concert publishing, booking monitoring, manual status override, flagging.
  Reuses Catalog/Booking services rather than duplicating logic.
- **Identity** — minimal: seeded `Users` table with a `Role`, current user resolved from an
  `X-User-Id` header via middleware. No passwords, no JWT (see [ASSUMPTIONS.md](ASSUMPTIONS.md)).

A `BackgroundService`, coordinated by a Redis leader-lock so it's safe if ever scaled to
multiple instances, sweeps expired reservation holds on a timer.

## Data model

- **User** — `Id`, `Name`, `Email`, `Role` (`Customer` | `Operator`). Seeded only.
- **Concert** — `Id`, `Title`, `Description`, `VenueName`, `StartTime`,
  `Status` (`Draft` | `Published` | `Cancelled`). Only `Published` concerts are visible to
  customers.
- **TicketCategory** — `Id`, `ConcertId`, `Name`, `Price`, `TotalQuantity`,
  `AvailableQuantity`. `CHECK (AvailableQuantity >= 0)` is a second line of defense against
  overselling, independent of the application-level locking.
- **Booking** — `Id`, `UserId`, `ConcertId`,
  `Status` (`PendingPayment` | `Confirmed` | `Cancelled` | `Expired`),
  `IdempotencyKey` (unique), `HoldExpiresAt`, `TotalAmount`, `VoucherId` (nullable),
  `IsFlagged`, `FlagReason` (nullable). One booking may cover multiple ticket categories.
- **BookingItem** — `Id`, `BookingId`, `TicketCategoryId`, `Quantity`, `UnitPrice`
  (snapshotted at booking time so later price changes don't affect existing bookings).
- **Voucher** — `Id`, `Code` (unique), `DiscountType` (`Percentage` | `FixedAmount`),
  `DiscountValue`, `MaxRedemptions`, `RedeemedCount`, `ValidFrom`, `ValidTo`,
  `ConcertId` (nullable — null means platform-wide). Seed data only.
- **VoucherRedemption** — `Id`, `VoucherId`, `BookingId`, `UserId`, `CreatedAt`. A unique
  constraint on `(VoucherId, UserId)` is the primary defense against voucher abuse, enforced
  at the DB level rather than only in application logic.

**Booking status semantics**: `PendingPayment` is set the instant inventory is reserved,
atomically, in the same transaction that decrements availability. `Confirmed` follows the
simulated payment call. `Expired` is set by the background sweep when `HoldExpiresAt` passes
without confirmation, releasing inventory and any voucher redemption in the same transaction.
`Cancelled` is an explicit customer/operator action. `IsFlagged` is orthogonal to `Status` —
operators can flag a booking as suspicious regardless of its lifecycle state, so "suspicious"
doesn't collide with the payment state machine.

## Booking flow & concurrency design

This is the core of the project. Three designs were considered:

1. **Postgres-first, Redis-as-accelerator (chosen)** — Postgres is the single source of
   truth; correctness comes from `SELECT ... FOR UPDATE` transactions and DB constraints.
   Redis accelerates specific paths (idempotency fast-path, rate limiting, background-job
   leader election, read-through caching of catalog endpoints) but is never load-bearing for
   correctness.
2. **Redis distributed lock in front of Postgres** — a Redis lock per `TicketCategory` would
   serialize attempts before they reach the DB. Rejected: the DB row lock already serializes
   writers for that row, so this mostly adds a second failure mode (lock TTL expiry, lock not
   released) without a meaningful upside at ~8 req/sec peak.
3. **Redis as hot-path source of truth, async persistence to Postgres** — an atomic Redis
   counter gates bookings, with async write-back to Postgres via an outbox/queue. This is the
   right answer at 10–100x the stated traffic, but introduces dual-write consistency risk,
   reconciliation logic, and crash-recovery concerns that are hard to build and verify
   correctly in a small project, for a scale that doesn't need it.

**Reservation flow** — `POST /api/bookings`, requires an `Idempotency-Key` header:

1. Redis `SETNX` fast-path check on the idempotency key. If a request with this key is
   in-flight or recently completed, short-circuit and return the cached result. This is a
   performance optimization only — not relied on for correctness.
2. Open a DB transaction. Lock every referenced `TicketCategory` row with
   `SELECT ... FOR UPDATE`, **sorted by `Id`** so lock acquisition order is consistent across
   concurrent multi-item bookings, preventing deadlocks.
3. Verify `AvailableQuantity >= requested` for each category; on failure, roll back and
   return `409 SOLD_OUT`.
4. Decrement `AvailableQuantity` per item.
5. If a `voucherCode` was supplied: lock the `Voucher` row `FOR UPDATE`, validate the
   validity window, `RedeemedCount < MaxRedemptions`, and that no `(VoucherId, UserId)`
   redemption already exists; increment `RedeemedCount`; insert `VoucherRedemption`.
6. Insert `Booking` (`Status = PendingPayment`, `HoldExpiresAt = now + 10 min`) and its
   `BookingItem` rows.
7. Commit. Return `201 Created`.
8. If the commit fails on the unique `IdempotencyKey` constraint (a race that slipped past
   the Redis fast-path in step 1), catch it and return the *existing* booking with
   `200 OK` — idempotent replay, with the DB constraint as the actual source of truth.

**Hold expiry** — a `BackgroundService` polls every 30s for `PendingPayment` bookings past
`HoldExpiresAt`, guarded by a Redis leader-lock (`SETNX` + TTL) so only one running instance
executes the sweep at a time. Each expiry runs in its own transaction: release
`AvailableQuantity`, roll back `RedeemedCount` / delete the `VoucherRedemption` if a voucher
was used, set `Status = Expired`.

**Payment confirmation** — `POST /api/bookings/{id}/confirm-payment` simulates a payment
success callback: `PendingPayment → Confirmed`, rejected if already expired/cancelled.

**Cancellation** — `POST /api/bookings/{id}/cancel` releases inventory the same way as
expiry, for a customer's own `PendingPayment` booking (or by an operator at any status via
the operator override endpoint).

**Rate limiting** — ASP.NET Core's built-in rate limiter (fixed-window, keyed by
`X-User-Id`) on the booking endpoint. This is in-memory/per-instance; a real multi-instance
deployment would need a Redis-backed limiter — documented as a limitation, not built now.

### Redis usage summary

Redis is used only where it has a concrete, defensible job:

- Idempotency-key fast-path cache (performance, not correctness).
- Fixed-window rate limiting input (if extended to distributed; currently in-memory).
- Leader-lock for the single hold-expiry `BackgroundService` instance.
- Read-through cache for catalog/browse endpoints (higher read volume than the booking
  endpoint itself, and safe to cache briefly since staleness there is low-risk).

## API surface

**Customer-facing** (`X-User-Id` header must resolve to a `Customer`):

- `GET /api/concerts` — list published concerts (paginated).
- `GET /api/concerts/{id}` — detail incl. ticket categories, price, live availability.
- `POST /api/bookings` — reserve tickets (+ optional voucher code), idempotent.
- `GET /api/bookings` — current user's bookings.
- `GET /api/bookings/{id}`.
- `POST /api/bookings/{id}/confirm-payment` — simulated payment success.
- `POST /api/bookings/{id}/cancel` — cancel own `PendingPayment` booking.

**Operator-facing** (`X-User-Id` must resolve to `Operator`, else `403`):

- `GET /api/operator/concerts` — list all concerts regardless of status (incl. `Draft`),
  filterable by status; the customer `GET /api/concerts` only ever returns `Published` ones,
  so operators need this separate view to see what's pending publish.
- `GET /api/operator/concerts/{id}` — detail incl. ticket categories and current
  `AvailableQuantity`, for any status. Covers "validate ticket availability".
- `POST /api/operator/concerts` — create (`Draft`).
- `PUT /api/operator/concerts/{id}` — update details.
- `POST /api/operator/concerts/{id}/ticket-categories` — add a category.
- `POST /api/operator/concerts/{id}/publish` — `Draft → Published`.
- `GET /api/operator/bookings` — monitor, filterable by status/concert/`IsFlagged`.
- `PUT /api/operator/bookings/{id}/status` — manual override (force-confirm/force-cancel).
- `POST /api/operator/bookings/{id}/flag` — mark suspicious with a reason.

Authentication is a seeded-user, header-based stand-in (`X-User-Id`) — no passwords, no JWT.
This is an explicit, documented simplification: the focus is booking-domain
design, not auth infrastructure.

## Testing strategy

- **Unit tests** (xUnit, `ConcertBooking.Tests/Unit/`) — reserved for logic that's genuinely
  pure (no I/O, no DbContext): currently just voucher discount calculation
  (`VoucherDiscountCalculatorTests.cs`). Status-transition rules and request validation are
  deliberately *not* here — they're written inline inside the same transactional service
  methods that touch the DB (e.g. `BookingService.ConfirmPaymentAsync`/`CancelAsync`,
  `OperatorConcertService`), not factored into standalone pure functions, so a mocked-DbContext
  unit test would mostly just be re-asserting the mock's own setup rather than proving anything.
- **Integration tests** (`ConcertBooking.Tests/Integration/`) — Testcontainers for .NET,
  spinning up real PostgreSQL + Redis containers. EF Core's InMemory provider is not used for
  anything concurrency-related, since it can't exercise real transaction/locking semantics.
  This is where status-transition rules and request validation actually get their coverage
  (`PaymentConfirmationTests.cs`, `BookingCancellationTests.cs`, `BookingCreationTests.cs`,
  `OperatorConcertWriteTests.cs`, ...), alongside the full booking → confirm → cancel
  lifecycle, voucher redemption limits, and the hold-expiry sweep.
- **Flagship concurrency test** — fire N concurrent booking requests (`Task.WhenAll`) against
  a `TicketCategory` seeded with a small `AvailableQuantity` (e.g. 5 requests × 3 tickets
  against 10 available). Assert: total tickets ever booked ≤ `TotalQuantity`, no negative
  `AvailableQuantity`, and that sending a duplicate `Idempotency-Key` twice produces exactly
  one `Booking` row. This is a direct, executable proof of the core concern
  (overselling/duplicates), stronger than a written explanation alone.

## Scope, assumptions & limitations

What's assumed, what's built, and what's deliberately out of scope is tracked separately in
[`docs/ASSUMPTIONS.md`](ASSUMPTIONS.md) rather than duplicated here.

