# Webhook Relay SaaS — Design (Project #3)

> Personal portfolio showpiece. Multi-tenant webhook relay/gateway API on the same
> .NET stack/conventions as receipts-api (#1) and fx-rates-api (#2).

## Context

This is the third and showcase portfolio project, built to land USD freelance/Upwork
backend work. #1 (receipts-api) and #2 (fx-rates-api) are shipped & live. #3 must
demonstrate the things SaaS-backend clients pay for: **multi-tenancy, API-key auth,
Stripe billing, a durable background job queue, quotas/rate-limits, and CI/CD** — all
deployable on the free tier (Render Docker web service + Neon Postgres, no Redis).

The chosen demo domain is a **webhook relay**: tenants register endpoints, push events
in, and the relay fans them out to those endpoints with retries, backoff, HMAC
signing, and full delivery tracking. This makes billing/quotas/queue meaningful and is
naturally demo-able (push an event, watch deliveries land), and reuses the
delivery/retry concepts already built in #2.

## Goals

- Real multi-tenant isolation (a tenant can never see another tenant's data).
- API-key authenticated data plane + JWT-authenticated control plane.
- Durable, restart-safe background queue with retries/backoff — **no extra infra**.
- Stripe test-mode Checkout + subscription webhooks driving per-plan quotas.
- Same conventions as #1/#2 so it reads as a coherent body of work.

## Non-goals (YAGNI)

- Usage-metered Stripe billing (report-usage). Plan-tier quotas only.
- Any UI/dashboard — API + Scalar docs only.
- Redis / external broker, multi-region, per-tenant schemas or databases.

## Architecture

Clean Architecture, four projects mirroring #2 exactly:

```
src/WebhookRelay.API             # minimal APIs, Program.cs, auth, Scalar transformers
src/WebhookRelay.Core            # entities, interfaces (ITokenIssuer, IPasswordHasher, IClock, IHttpDelivery), services
src/WebhookRelay.Infrastructure  # DbContext, EF config, migrations, Dispatcher BackgroundService, Stripe + HTTP clients
tests/WebhookRelay.Tests         # xUnit + WebApplicationFactory integration tests, Fakes/
WebhookRelayAPI.slnx
```

Target `net10.0`, Nullable + ImplicitUsings enabled. Package versions match #2
(EF Core 10, Npgsql 10, JwtBearer 10, Scalar 2.16.x, BCrypt.Net-Next 4.2, xUnit 2.9,
Sqlite 10 for tests) plus **Stripe.net** for billing.

### Two planes / auth

- **Control plane (JWT, 2h):** register → create Tenant → login. Manage API keys,
  endpoints, billing; read delivery logs. JWT carries `sub` (user id) + `tenant` claim.
- **Data plane (API key):** `POST /v1/events` authenticates with `whk_live_…` sent as
  `Authorization: Bearer` or `X-API-Key`. Keys **hashed at rest** (BCrypt or SHA-256
  of the secret); only a short prefix stored in clear for display; full secret shown
  once at creation. Custom auth handler resolves the key → tenant.

### Tenant isolation

Shared DB. Every tenant-owned entity carries `TenantId`. A **resolved-tenant accessor**
(scoped, set by middleware/auth from JWT or API key) feeds an **EF Core global query
filter** so all reads are automatically tenant-scoped. Writes stamp `TenantId` from the
same accessor.

## Data model

All tenant-scoped except `Tenant` / `User`.

- **Tenant** — `Plan` (Free|Pro), `StripeCustomerId`, `StripeSubscriptionId`,
  `SubscriptionStatus`, timestamps.
- **User** — belongs to Tenant, email + BCrypt password hash, JWT login.
- **ApiKey** — `TenantId`, `Hash`, `Prefix`, `Label`, `LastUsedAt`, `RevokedAt`.
- **Endpoint** — `TenantId`, `Url`, `SigningSecret` (per-endpoint), `IsActive`,
  `EventTypeFilter` (null = all).
- **Event** — `TenantId`, `Type`, `Payload` (jsonb/text), `IdempotencyKey?`, `CreatedAt`.
- **Delivery** — `TenantId`, `EventId`, `EndpointId`, `Status`
  (`Pending|Delivering|Succeeded|Failed|Dead`), `Attempts`, `NextAttemptAt`,
  `LastStatusCode`, `LastResponseSnippet`, timestamps. **This is the queue table.**

Decimal/precision and fluent config in `OnModelCreating`, indexes on
`(Status, NextAttemptAt)` for the claim query and on `TenantId` per table.

## Data flow

1. **Ingest** — `POST /v1/events` (API key) → resolve tenant → check daily-event quota
   for plan → if `IdempotencyKey` seen for tenant, return prior event (dedupe) → insert
   `Event` + one `Delivery` (`Pending`) per active endpoint whose filter matches →
   `202 Accepted` + event id.
2. **Dispatch** — `Dispatcher : BackgroundService` (`PeriodicTimer`, try/catch loop like
   #2's `RateRefreshService`) claims a batch:
   `SELECT … FOR UPDATE SKIP LOCKED` over due deliveries (`Pending`/retryable with
   `NextAttemptAt <= now`), marks `Delivering`, POSTs payload with
   `X-Webhook-Signature: sha256=<hmac>` (HMAC-SHA256 over body using endpoint secret) +
   `X-Webhook-Id`/`X-Webhook-Event` headers via an injected `IHttpDelivery` client.
3. **Result** — 2xx → `Succeeded`. Non-2xx/timeout → increment `Attempts`, set
   `NextAttemptAt = now + backoff(attempts)` (exponential, capped); past max attempts →
   `Dead`.
4. **Read/replay** — tenant lists/reads deliveries; `POST /v1/deliveries/{id}/replay`
   re-queues a `Delivery` (resets to `Pending`, `NextAttemptAt = now`).

## Billing & quotas (Stripe test mode)

- Plans in config: **Free** (e.g. 2 endpoints, 100 events/day) and **Pro** (e.g. 20
  endpoints, 10k events/day).
- `POST /v1/billing/checkout` (JWT) → create Stripe Checkout session → return URL.
- `POST /v1/billing/webhook` → **verify Stripe signature** → handle
  `checkout.session.completed`, `customer.subscription.updated`,
  `customer.subscription.deleted` → set `Tenant.Plan` + `SubscriptionStatus`.
  **Idempotent** by Stripe event id (store processed ids / upsert).
- `GET /v1/billing` (JWT) → current plan, status, today's usage vs limits.
- **Quota enforcement:** a service checks daily event count + endpoint count against the
  tenant's plan before ingest/endpoint-create → `429` (+ `Retry-After`) on event cap,
  `403` on endpoint cap.

## API surface

Data plane = API key; everything else = JWT.

- `POST /auth/register`, `POST /auth/login`
- `POST /v1/keys`, `GET /v1/keys`, `DELETE /v1/keys/{id}`
- `POST /v1/endpoints`, `GET /v1/endpoints`, `PATCH /v1/endpoints/{id}`, `DELETE /v1/endpoints/{id}`
- `POST /v1/events` *(API key)*, `GET /v1/events/{id}`
- `GET /v1/deliveries`, `GET /v1/deliveries/{id}`, `POST /v1/deliveries/{id}/replay`
- `POST /v1/billing/checkout`, `POST /v1/billing/webhook`, `GET /v1/billing`
- `GET /health`

## Error handling

- Validation → `400` with problem details; auth fail → `401`; quota/permission →
  `403`/`429`; unknown id within tenant → `404`.
- Dispatcher never throws out of the loop (logged, retried next tick) — matches #2.
- All-failure delivery path ends in `Dead`, never crashes ingestion.

## Testing (TDD, WebApplicationFactory)

SQLite in-memory test DB via `TestWebApplicationFactory` (mirror #2). `Testing`
environment skips Postgres, the real Dispatcher hosted service, and the real HTTP
delivery client; tests drive dispatch manually (expose a `DispatchOnceAsync()` like #2's
`RefreshAsync()`). Fakes in `tests/.../Fakes/`: `FakeClock`, `FakeHttpDelivery`
(scriptable success/failure + records sends), fake Stripe gateway.

Coverage:
- **Auth:** register/login; JWT-gated routes reject anon (`401`).
- **API keys:** create returns secret once; hashed at rest; revoked key → `401`.
- **Isolation:** tenant A cannot read B's keys/endpoints/events/deliveries (`404`/empty).
- **Ingestion:** `202` + one delivery per active matching endpoint; event-type filter
  respected; `IdempotencyKey` dedupes.
- **Dispatcher:** claimed delivery POSTs with a **valid HMAC signature** to the fake
  receiver; 2xx → `Succeeded`; failing receiver → `Attempts` increment + backoff →
  `Dead`; replay re-queues.
- **Quotas:** over daily event cap → `429`; over endpoint cap → `403`.
- **Stripe webhook:** bad signature → `400`; valid `subscription.updated` flips plan;
  duplicate Stripe event id is idempotent. Stripe client faked.

## Deployment (same stack as #1/#2)

- Multi-stage **Dockerfile** (copy #2; rename projects).
- **render.yaml** — Render Docker web service, `plan: free`, `healthCheckPath: /health`;
  env vars `DATABASE_URL` (Neon, `sync:false`), `Jwt__Key` (`generateValue`),
  `Stripe__SecretKey` + `Stripe__WebhookSecret` (`sync:false`),
  `Stripe__PriceId` (Pro plan price).
- EF migrations applied on startup (`Database.Migrate()`, non-Testing).
- Scalar docs at `/scalar`; **`X-Forwarded-Proto` forwarded-headers fix copied verbatim
  from #2's Program.cs** (clear KnownProxies/KnownIPNetworks).
- **GitHub Actions:** CI (restore/build/test) + keepalive pinging `/health` every 12 min
  via `HEALTHCHECK_URL` repo variable.
- **Git identity:** repo-local `user.name`/`user.email = suzerai1302` (+ noreply email),
  commit only from inside the project dir, **no `Co-Authored-By` trailers**.

## Build order (vertical slices, TDD)

1. Solution skeleton + health endpoint + test harness (red→green).
2. Auth: register/login + JWT + tenant creation.
3. API keys: create/list/revoke + API-key auth handler + isolation.
4. Endpoints CRUD (+ tenant isolation, event-type filter field).
5. Event ingestion → Delivery rows (+ idempotency).
6. Dispatcher: claim/deliver/sign/retry/backoff/dead + replay.
7. Quotas + plan limits (429/403).
8. Stripe: checkout + signed webhook → plan flips (idempotent) + `GET /v1/billing`.
9. Deployment files (Docker, render.yaml, CI, keepalive) + README + Scalar polish.

Each slice = its own GitHub issue (via `to-issues`), built test-first.
