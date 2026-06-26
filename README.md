# Webhook Relay API

A multi-tenant **webhook relay SaaS**: tenants register HTTP endpoints, push events, and the
relay fans each event out to the matching endpoints with HMAC signing, retries with
exponential backoff, dead-lettering, and per-delivery tracking. Plans and quotas are gated by
Stripe billing. Built with ASP.NET Core and Entity Framework Core.

> **Live demo:** https://webhook-relay-api.onrender.com · **Interactive API docs:** [`/scalar`](https://webhook-relay-api.onrender.com/scalar)
>
> _Hosted on Render's free tier — the first request after ~15 min idle takes ~50s to wake, then it's fast._

![CI](https://github.com/suzerai1302/webhook-relay-api/actions/workflows/ci.yml/badge.svg)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## Why this project

Reliable webhook delivery is a deceptively hard backend problem: you need per-tenant
isolation, at-least-once delivery with retries and backoff, signature verification so
receivers can trust you, idempotency so retries don't double-fire, dead-lettering and
replay for endpoints that stay down, and quota/billing so the platform pays for itself.
This service implements that core — the unglamorous machinery behind every "Settings →
Webhooks" page — as a small, fully-tested SaaS.

## Features

- **Multi-tenant** — every row is tenant-scoped via an EF Core global query filter; one tenant can never read another's data
- **Two-plane auth** — JWT bearer for the control plane (manage endpoints, inspect deliveries); per-tenant API keys (`whk_live_…`) for the high-volume data plane (ingest)
- **Event fan-out** — one ingested event creates one delivery per active endpoint whose type filter matches
- **Idempotent ingest** — an `Idempotency-Key` header dedupes retried pushes to a single event
- **Signed, reliable delivery** — a background dispatcher claims due deliveries (`FOR UPDATE SKIP LOCKED` on Postgres), HMAC-SHA256 signs the body, and retries with exponential backoff up to a cap, then dead-letters
- **Replay** — re-queue a dead/failed delivery once the endpoint recovers
- **Plans & quotas** — per-plan caps on active endpoints (`403`) and events/day (`429` + `Retry-After`)
- **Stripe billing** — hosted Checkout to upgrade; a signature-verified, idempotent webhook flips the plan
- **Interactive docs** — OpenAPI + Scalar UI with Authorize boxes for both schemes
- **Fully tested** — integration tests over real HTTP via `WebApplicationFactory`, no network

## Tech stack

| Concern | Choice |
|---|---|
| Framework | ASP.NET Core (.NET 10), Minimal APIs |
| Persistence | Entity Framework Core + PostgreSQL |
| Auth | JWT bearer + custom API-key scheme, BCrypt password hashing |
| Billing | Stripe.net (Checkout + webhook signature verification) |
| Docs | OpenAPI + Scalar |
| Tests | xUnit + `WebApplicationFactory`, SQLite in-memory |
| CI / Deploy | GitHub Actions · Render (Docker) + Neon (Postgres) |

## Architecture

Clean Architecture — dependencies point inward only:

- **`WebhookRelay.Core`** — entities, ports (abstractions), and pure services: `SignatureService` (HMAC) and `BackoffPolicy`
- **`WebhookRelay.Infrastructure`** — EF Core `DbContext` (with the tenant query filter), the `DeliveryProcessor`, the real `HttpDelivery` sender, and the background `Dispatcher`
- **`WebhookRelay.API`** — endpoints, DI, the two auth schemes, the Stripe gateway, and OpenAPI

Ingest writes an event plus a `Pending` delivery per matching endpoint. A hosted
`Dispatcher` ticks on an interval, and each tick the `DeliveryProcessor` claims the due
rows, signs and POSTs them, and on a non-2xx increments the attempt count and reschedules
with backoff — or marks the delivery `Dead` once attempts are exhausted. Background work
runs with no tenant context, so it bypasses the query filter explicitly.

## Endpoints

| Method | Route | Auth | Description |
|---|---|---|---|
| GET | `/health` | — | Liveness |
| POST | `/auth/register` | — | Create account + tenant (201) |
| POST | `/auth/login` | — | Get a JWT |
| POST | `/v1/keys` | JWT | Create an API key — secret returned **once** |
| GET | `/v1/keys` | JWT | List your API keys (no secrets) |
| DELETE | `/v1/keys/{id}` | JWT | Revoke an API key |
| GET | `/v1/whoami` | API key | Resolve the tenant an API key belongs to |
| POST | `/v1/endpoints` | JWT | Register an endpoint (`403` over plan quota) |
| GET | `/v1/endpoints` | JWT | List endpoints |
| PATCH | `/v1/endpoints/{id}` | JWT | Update url / active / type filter |
| DELETE | `/v1/endpoints/{id}` | JWT | Delete an endpoint |
| POST | `/v1/events` | API key | Ingest an event (`429`+`Retry-After` over quota; `Idempotency-Key` dedupes) |
| GET | `/v1/events/{id}` | JWT | Event detail + its deliveries |
| GET | `/v1/deliveries` | JWT | List deliveries |
| GET | `/v1/deliveries/{id}` | JWT | Delivery detail |
| POST | `/v1/deliveries/{id}/replay` | JWT | Re-queue a delivery |
| POST | `/v1/billing/checkout` | JWT | Start a Stripe Checkout session → `{ url }` |
| POST | `/v1/billing/webhook` | Stripe sig | Stripe events (idempotent); flips the plan |
| GET | `/v1/billing` | JWT | Current plan, status, and usage vs limits |

Each delivery carries `X-Webhook-Id`, `X-Webhook-Event`, and an `X-Webhook-Signature`
(`sha256=…`) computed with the endpoint's per-endpoint signing secret.

## Run locally

```bash
# needs the .NET 10 SDK
dotnet test                                       # run the test suite
dotnet run --project src/WebhookRelay.API         # needs a Postgres connection (see below)
```

The API uses Postgres outside the test environment. Provide a connection string via
`ConnectionStrings__Postgres` or a `DATABASE_URL` (a `postgres://…` URL is parsed
automatically), and a signing key via `Jwt__Key`. Stripe billing needs `Stripe__SecretKey`,
`Stripe__WebhookSecret`, and `Stripe__PriceId`. Tests need none of these — they run on
SQLite in-memory with fakes for the clock, HTTP sender, and Stripe.

## Deploy (Render + Neon)

1. Create a free **Neon** Postgres project; copy its connection string.
2. In **Render**, create a **Blueprint** from this repo (`render.yaml`) — a free Docker
   web service. Paste the Neon string as `DATABASE_URL`; `Jwt__Key` is generated. Set the
   `Stripe__*` vars from your Stripe test dashboard (secret key, a recurring Pro `price_…`,
   and the signing secret of a webhook endpoint pointed at `/v1/billing/webhook`).
3. After it's live, set the repo Actions **variable** `HEALTHCHECK_URL` to the Render base
   URL so the keepalive workflow pings `/health` every 12 minutes (also keeps the dispatcher
   warm). Then drop the URL into the live-demo line at the top.

Migrations apply automatically on startup; the `X-Forwarded-Proto` fix ensures the
OpenAPI/Scalar docs work behind Render's TLS proxy.

## License

MIT — see [LICENSE](LICENSE).
