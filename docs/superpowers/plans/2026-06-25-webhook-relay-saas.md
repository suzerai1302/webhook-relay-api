# Webhook Relay SaaS Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a multi-tenant webhook relay SaaS API — tenants register endpoints, push events, the relay fans them out with retries/HMAC signing/delivery tracking, gated by Stripe-driven per-plan quotas.

**Architecture:** Clean Architecture (API / Core / Infrastructure / Tests) on .NET 10, mirroring the sibling project `../fx-rates-api` exactly. JWT control plane + SHA-256 API-key data plane. Shared Postgres DB with EF Core global query filter for tenant isolation. Durable background dispatcher using `FOR UPDATE SKIP LOCKED`. Stripe test-mode Checkout + signed webhooks.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + Npgsql, JwtBearer, BCrypt.Net-Next (passwords), SHA-256 (API keys), Stripe.net, Scalar, xUnit + WebApplicationFactory + SQLite (tests). Deploy: Docker → Render + Neon.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-06-25-webhook-relay-saas-design.md` — authoritative.
- **Reference project:** `C:\Work\Personal\smart\projects\fx-rates-api` — copy conventions/boilerplate verbatim; namespaces become `WebhookRelay.*`, names `WebhookRelay`.
- **Target framework:** `net10.0`, `Nullable=enable`, `ImplicitUsings=enable` on all projects. Package versions match fx-rates-api's `.csproj` files.
- **Git identity:** repo already inited with `user.name=suzerai1302`, `user.email=suzerai1302@users.noreply.github.com`. Commit only from inside this dir. **NEVER add `Co-Authored-By` trailers.**
- **Testing env:** `builder.Environment.IsEnvironment("Testing")` gates out Postgres, the Dispatcher hosted service, and the real HTTP delivery client (copy #2's Program.cs conditional pattern). `public partial class Program { }` at end of Program.cs for `WebApplicationFactory<Program>`.
- **Render proxy:** copy the `X-Forwarded-Proto` ForwardedHeaders block verbatim from `fx-rates-api/src/FxRates.API/Program.cs` (clears `KnownIPNetworks`/`KnownProxies`).
- **Plans:** Free = 2 endpoints / 100 events per day. Pro = 20 endpoints / 10000 events per day. Limits live in config keyed by plan.
- **TDD:** every task is red → green → commit. Run `dotnet test` from repo root.

## File Structure

```
WebhookRelayAPI.slnx
src/WebhookRelay.Core/
  Entities/        Tenant.cs User.cs ApiKey.cs Endpoint.cs Event.cs Delivery.cs (+ enums Plan, DeliveryStatus)
  Abstractions/    ITokenIssuer.cs IPasswordHasher.cs IApiKeyHasher.cs IClock.cs IHttpDelivery.cs ITenantContext.cs IStripeGateway.cs IPlanCatalog.cs
  Services/        SignatureService.cs BackoffPolicy.cs PlanCatalog.cs
src/WebhookRelay.Infrastructure/
  WebhookRelayDbContext.cs
  Migrations/
  Dispatcher.cs            (BackgroundService)
  DeliveryProcessor.cs     (claim+deliver logic, callable from tests)
  HttpDelivery.cs          (IHttpDelivery via HttpClient)
  StripeGateway.cs         (IStripeGateway via Stripe.net)
  SystemClock.cs
src/WebhookRelay.API/
  Program.cs
  JwtTokenIssuer.cs BcryptPasswordHasher.cs Sha256ApiKeyHasher.cs
  TenantContext.cs          (scoped, holds resolved TenantId)
  ApiKeyAuthHandler.cs      (AuthenticationHandler for data plane)
  Endpoints/                Auth.cs Keys.cs Endpoints.cs Events.cs Deliveries.cs Billing.cs Health.cs
  BearerSecuritySchemeTransformer.cs (copy from #2)
tests/WebhookRelay.Tests/
  TestWebApplicationFactory.cs
  Fakes/  FakeClock.cs FakeHttpDelivery.cs FakeStripeGateway.cs
  *Tests.cs (one file per slice)
Dockerfile  render.yaml  .github/workflows/ci.yml  .github/workflows/keepalive.yml  README.md  LICENSE
```

---

### Task 1: Solution skeleton + health endpoint + test harness

**Files:**
- Create: `WebhookRelayAPI.slnx`, the 4 `.csproj` files, `src/WebhookRelay.API/Program.cs`, `tests/WebhookRelay.Tests/TestWebApplicationFactory.cs`, `tests/WebhookRelay.Tests/HealthTests.cs`
- Reference (copy + rename): all of `fx-rates-api/{*.slnx,src,tests}` scaffolding

**Interfaces:**
- Produces: a running API with `GET /health` → `200 {status:"ok"}`; `TestWebApplicationFactory : WebApplicationFactory<Program>` using SQLite in-memory; `public partial class Program {}`.

- [ ] **Step 1: Scaffold projects.** Run from repo root:
```bash
dotnet new web -n WebhookRelay.API -o src/WebhookRelay.API -f net10.0
dotnet new classlib -n WebhookRelay.Core -o src/WebhookRelay.Core -f net10.0
dotnet new classlib -n WebhookRelay.Infrastructure -o src/WebhookRelay.Infrastructure -f net10.0
dotnet new xunit -n WebhookRelay.Tests -o tests/WebhookRelay.Tests -f net10.0
```
Create `WebhookRelayAPI.slnx` by copying `fx-rates-api/FxRatesAPI.slnx` and replacing `FxRates`→`WebhookRelay`. Add project references: API→Core+Infrastructure, Infrastructure→Core, Tests→API. Copy PackageReferences from each matching `fx-rates-api` `.csproj` (add `Stripe.net` to API later in Task 8). Set `Nullable`/`ImplicitUsings` enable everywhere; add `<Using Include="Xunit"/>` to Tests.

- [ ] **Step 2: Write the failing test.** `tests/WebhookRelay.Tests/HealthTests.cs`:
```csharp
public class HealthTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    public HealthTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var res = await _factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
```
Write a minimal `TestWebApplicationFactory` copied from `fx-rates-api/tests/FxRates.Tests/TestWebApplicationFactory.cs` but trimmed to: `UseEnvironment("Testing")`, open a `SqliteConnection("DataSource=:memory:")`, register `AddDbContext<WebhookRelayDbContext>(o=>o.UseSqlite(conn))`, `EnsureCreated()`. (DbContext is created in Task 2 — for now create an empty `WebhookRelayDbContext` with just the ctor so this compiles.)

- [ ] **Step 3: Run test to verify it fails.** Run: `dotnet test`. Expected: FAIL (build error / 404) before Program.cs wiring.

- [ ] **Step 4: Implement Program.cs.** Copy `fx-rates-api/src/FxRates.API/Program.cs` and strip to: PORT binding, `isTesting` flag, OpenAPI+Scalar, the `DATABASE_URL`→Npgsql parse + `AddDbContext` (non-Testing), the **verbatim ForwardedHeaders block**, `Database.Migrate()` (non-Testing), `app.MapOpenApi(); app.MapScalarApiReference();`, `app.MapGet("/health", () => Results.Ok(new { status = "ok" }));`, and `public partial class Program {}`. Remove all FX-specific services.

- [ ] **Step 5: Run tests to verify pass.** Run: `dotnet test`. Expected: PASS.

- [ ] **Step 6: Commit.**
```bash
git add -A && git commit -m "feat: solution skeleton + health endpoint + test harness"
```

---

### Task 2: Domain entities + DbContext + tenant context + initial migration

**Files:**
- Create: `src/WebhookRelay.Core/Entities/*.cs` (Tenant, User, ApiKey, Endpoint, Event, Delivery + enums `Plan`, `DeliveryStatus`), `src/WebhookRelay.Core/Abstractions/ITenantContext.cs`, `src/WebhookRelay.API/TenantContext.cs`, `src/WebhookRelay.Infrastructure/WebhookRelayDbContext.cs`, `src/WebhookRelay.Infrastructure/SystemClock.cs`, `src/WebhookRelay.Core/Abstractions/IClock.cs`
- Test: `tests/WebhookRelay.Tests/TenantIsolationDbTests.cs`

**Interfaces:**
- Produces:
  - `enum Plan { Free, Pro }`; `enum DeliveryStatus { Pending, Delivering, Succeeded, Failed, Dead }`
  - Entities with `Guid Id` PKs. `Tenant { Guid Id; Plan Plan; string? StripeCustomerId; string? StripeSubscriptionId; string? SubscriptionStatus; DateTime CreatedAt; }`. `User { Guid Id; Guid TenantId; string Email; string PasswordHash; }`. `ApiKey { Guid Id; Guid TenantId; string Hash; string Prefix; string Label; DateTime? LastUsedAt; DateTime? RevokedAt; DateTime CreatedAt; }`. `Endpoint { Guid Id; Guid TenantId; string Url; string SigningSecret; bool IsActive; string? EventTypeFilter; DateTime CreatedAt; }`. `Event { Guid Id; Guid TenantId; string Type; string Payload; string? IdempotencyKey; DateTime CreatedAt; }`. `Delivery { Guid Id; Guid TenantId; Guid EventId; Guid EndpointId; DeliveryStatus Status; int Attempts; DateTime? NextAttemptAt; int? LastStatusCode; string? LastResponseSnippet; DateTime CreatedAt; DateTime UpdatedAt; }`
  - `interface ITenantContext { Guid? TenantId { get; set; } }`; `TenantContext` scoped impl.
  - `WebhookRelayDbContext` with `DbSet`s for all entities, a **global query filter** `e => e.TenantId == _tenant.TenantId` on every tenant-scoped entity, indexes on `(Status, NextAttemptAt)` for Delivery and `TenantId` per table, unique index `(TenantId, IdempotencyKey)` on Event.
  - `interface IClock { DateTime UtcNow { get; } }`; `SystemClock`.

- [ ] **Step 1: Write the failing test.** `TenantIsolationDbTests.cs` — insert two tenants' endpoints, set `ITenantContext.TenantId` to tenant A, assert the DbSet only returns A's rows:
```csharp
[Fact]
public async Task QueryFilter_HidesOtherTenantsRows()
{
    await using var f = new TestWebApplicationFactory();
    var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
    using (var scope = f.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<WebhookRelayDbContext>();
        var tc = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.TenantId = null; // bypass filter for seeding via IgnoreQueryFilters on add is N/A; add directly
        db.Endpoints.Add(new Endpoint { Id = Guid.NewGuid(), TenantId = a, Url = "https://a", SigningSecret = "s", IsActive = true });
        db.Endpoints.Add(new Endpoint { Id = Guid.NewGuid(), TenantId = b, Url = "https://b", SigningSecret = "s", IsActive = true });
        await db.SaveChangesAsync();
    }
    using (var scope = f.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<WebhookRelayDbContext>();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().TenantId = a;
        var urls = db.Endpoints.Select(e => e.Url).ToList();
        Assert.Equal(new[] { "https://a" }, urls);
    }
}
```

- [ ] **Step 2: Run test to verify it fails.** Run: `dotnet test --filter TenantIsolationDbTests`. Expected: FAIL (types/filter missing).

- [ ] **Step 3: Implement entities, enums, ITenantContext/TenantContext, IClock/SystemClock, and DbContext.** DbContext takes `(DbContextOptions, ITenantContext tenant)`; apply `entity.HasQueryFilter(e => e.TenantId == tenant.TenantId)` for each tenant-scoped entity in `OnModelCreating`. Register `ITenantContext`→`TenantContext` (scoped), `IClock`→`SystemClock` (singleton) in Program.cs; in `TestWebApplicationFactory` also register them so the test scope resolves them.

- [ ] **Step 4: Create the EF migration.** Run:
```bash
dotnet ef migrations add InitialCreate --project src/WebhookRelay.Infrastructure --startup-project src/WebhookRelay.API
```
(Migration is for Postgres/runtime; tests use `EnsureCreated()` on SQLite.)

- [ ] **Step 5: Run tests to verify pass.** Run: `dotnet test --filter TenantIsolationDbTests`. Expected: PASS.

- [ ] **Step 6: Commit.**
```bash
git add -A && git commit -m "feat: domain entities, DbContext with tenant query filter, initial migration"
```

---

### Task 3: Auth — register/login, JWT, tenant creation

**Files:**
- Create: `src/WebhookRelay.Core/Abstractions/{ITokenIssuer.cs,IPasswordHasher.cs}`, `src/WebhookRelay.API/{JwtTokenIssuer.cs,BcryptPasswordHasher.cs}`, `src/WebhookRelay.API/Endpoints/Auth.cs`
- Test: `tests/WebhookRelay.Tests/AuthTests.cs`

**Interfaces:**
- Consumes: `User`, `Tenant`, `WebhookRelayDbContext`.
- Produces: `ITokenIssuer.CreateToken(User user)` returning JWT with claims `sub`=user id and `tenant`=tenant id (2h expiry). `IPasswordHasher.Hash/Verify`. Routes `POST /auth/register` (body `{email,password}` → creates Tenant + User → `201 {token}`) and `POST /auth/login` (→ `200 {token}` or `401`). JWT auth wired in Program.cs (copy #2's `AddAuthentication().AddJwtBearer(...)`).

- [ ] **Step 1: Write failing tests.** `AuthTests.cs`: register returns 201 + token; duplicate email → 409; login wrong password → 401; login ok → 200 + token; the token's `tenant` claim is present.
```csharp
[Fact] public async Task Register_Returns201_WithToken() { /* POST /auth/register, assert 201 + json has token */ }
[Fact] public async Task Login_WrongPassword_Returns401() { /* ... */ }
```

- [ ] **Step 2: Run to verify fail.** Run: `dotnet test --filter AuthTests`. Expected: FAIL (404).

- [ ] **Step 3: Implement.** Copy `JwtTokenIssuer`/`BcryptPasswordHasher` from #2; add `tenant` claim. `Auth.cs` maps the two routes: register creates `Tenant{Plan=Free}` + `User`, hashes password, issues token. Register services + JWT bearer in Program.cs; set `Jwt:Key/Issuer/Audience` in `appsettings.json` + `appsettings.Testing` (or factory config) so tests can validate tokens. Add `app.UseAuthentication(); app.UseAuthorization();`.

- [ ] **Step 4: Run to verify pass.** Run: `dotnet test --filter AuthTests`. Expected: PASS.

- [ ] **Step 5: Commit.** `git add -A && git commit -m "feat: auth (register/login, JWT, tenant creation)"`

---

### Task 4: API keys + API-key auth handler + tenant resolution

**Files:**
- Create: `src/WebhookRelay.Core/Abstractions/IApiKeyHasher.cs`, `src/WebhookRelay.API/Sha256ApiKeyHasher.cs`, `src/WebhookRelay.API/ApiKeyAuthHandler.cs`, `src/WebhookRelay.API/Endpoints/Keys.cs`; modify Program.cs to set `ITenantContext.TenantId` from the JWT `tenant` claim (a middleware or per-endpoint resolution) and to register the API-key auth scheme.
- Test: `tests/WebhookRelay.Tests/ApiKeyTests.cs`

**Interfaces:**
- Consumes: `ApiKey`, `ITenantContext`, `IClock`.
- Produces: `IApiKeyHasher.Hash(string secret)` → SHA-256 hex. Routes (JWT): `POST /v1/keys` (`{label}` → `201 {id,prefix,secret}` — **secret shown once**), `GET /v1/keys` (→ list w/o secret), `DELETE /v1/keys/{id}` (sets `RevokedAt` → `204`). An `AuthenticationHandler` named scheme `"ApiKey"` that reads `X-API-Key` or `Authorization: Bearer whk_...`, hashes it, finds a non-revoked `ApiKey` (query with filters bypassed — handler must look up across tenants by hash), sets `ITenantContext.TenantId`, builds a `ClaimsPrincipal`. Generation: `whk_live_` + 32 random url-safe bytes; `Prefix` = first 12 chars; store SHA-256 of full secret.

**JWT→tenant wiring:** add middleware after `UseAuthentication` — if `User` has a `tenant` claim, set `ITenantContext.TenantId`. This makes the query filter active for all JWT routes.

- [ ] **Step 1: Write failing tests.** create key returns secret once + prefix; listing never returns secret; revoked key rejected on data plane (use a trivial `[Authorize(AuthenticationSchemes="ApiKey")]` test endpoint or defer to Task 5's ingest — here assert the handler via a temporary `GET /v1/keys` cross-tenant isolation: tenant B cannot see A's keys).
```csharp
[Fact] public async Task CreateKey_ReturnsSecretOnce_AndPrefix() { /* ... */ }
[Fact] public async Task ListKeys_OmitsSecret() { /* ... */ }
[Fact] public async Task OtherTenant_CannotSeeKeys() { /* register 2 tenants, assert isolation */ }
```

- [ ] **Step 2: Run to verify fail.** Run: `dotnet test --filter ApiKeyTests`. Expected: FAIL.

- [ ] **Step 3: Implement** hasher, key generation, `Keys.cs` routes, `ApiKeyAuthHandler`, JWT→tenant middleware. Register the api-key scheme via `AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>("ApiKey", null)` alongside JWT. The handler must query `ApiKey` with `IgnoreQueryFilters()` (no tenant set yet).

- [ ] **Step 4: Run to verify pass.** Run: `dotnet test --filter ApiKeyTests`. Expected: PASS.

- [ ] **Step 5: Commit.** `git commit -am "feat: API keys + API-key auth handler + tenant resolution"`

---

### Task 5: Endpoints CRUD

**Files:**
- Create: `src/WebhookRelay.API/Endpoints/Endpoints.cs`
- Test: `tests/WebhookRelay.Tests/EndpointsTests.cs`

**Interfaces:**
- Consumes: `Endpoint`, `ITenantContext`.
- Produces: JWT routes `POST /v1/endpoints` (`{url, eventTypeFilter?}` → generates a per-endpoint `SigningSecret` (random hex), `IsActive=true`, `201`), `GET /v1/endpoints`, `PATCH /v1/endpoints/{id}` (`{url?,isActive?,eventTypeFilter?}` → `200`/`404`), `DELETE /v1/endpoints/{id}` (`204`/`404`). Tenant-scoped automatically via query filter.

- [ ] **Step 1: Write failing tests.** create → 201 + has signingSecret; list scoped to tenant; patch toggles isActive; delete → 404 thereafter; other tenant gets 404 on patch/delete of foreign id.

- [ ] **Step 2: Run to verify fail.** `dotnet test --filter EndpointsTests`. Expected: FAIL.

- [ ] **Step 3: Implement** `Endpoints.cs`. Generate `SigningSecret` via 32 random bytes hex.

- [ ] **Step 4: Run to verify pass.** Expected: PASS.

- [ ] **Step 5: Commit.** `git commit -am "feat: endpoints CRUD"`

---

### Task 6: Event ingestion → delivery rows + idempotency

**Files:**
- Create: `src/WebhookRelay.API/Endpoints/Events.cs`
- Test: `tests/WebhookRelay.Tests/IngestTests.cs`

**Interfaces:**
- Consumes: `Event`, `Delivery`, `Endpoint`, `ITenantContext`, `IClock`.
- Produces: `POST /v1/events` (**ApiKey scheme**) body `{type, payload}` + optional `Idempotency-Key` header → creates `Event` + one `Delivery{Status=Pending, NextAttemptAt=now}` per active endpoint whose `EventTypeFilter` is null or equals `type` → `202 {eventId}`. If `Idempotency-Key` already used for this tenant, return the prior event's id (no new rows). `GET /v1/events/{id}` (JWT) → event + its deliveries.

- [ ] **Step 1: Write failing tests.** ingest with 2 active endpoints (one filtered out) → 202, exactly 1 matching delivery for filtered type; ingest with same Idempotency-Key twice → second creates no new event/deliveries (assert counts); ingest with bad/missing api key → 401.

- [ ] **Step 2: Run to verify fail.** `dotnet test --filter IngestTests`. Expected: FAIL.

- [ ] **Step 3: Implement** `Events.cs`. Use the unique `(TenantId, IdempotencyKey)` index; on dedupe, look up existing event and return its id.

- [ ] **Step 4: Run to verify pass.** Expected: PASS.

- [ ] **Step 5: Commit.** `git commit -am "feat: event ingestion -> delivery rows + idempotency"`

---

### Task 7: Dispatcher — claim, sign, deliver, retry/backoff, dead, replay

**Files:**
- Create: `src/WebhookRelay.Core/Abstractions/IHttpDelivery.cs`, `src/WebhookRelay.Core/Services/{SignatureService.cs,BackoffPolicy.cs}`, `src/WebhookRelay.Infrastructure/{DeliveryProcessor.cs,Dispatcher.cs,HttpDelivery.cs}`, `src/WebhookRelay.API/Endpoints/Deliveries.cs`, `tests/WebhookRelay.Tests/Fakes/{FakeClock.cs,FakeHttpDelivery.cs}`
- Test: `tests/WebhookRelay.Tests/DispatcherTests.cs`

**Interfaces:**
- Consumes: `Delivery`, `Endpoint`, `Event`, `IClock`.
- Produces:
  - `interface IHttpDelivery { Task<DeliveryResult> SendAsync(string url, string body, IDictionary<string,string> headers, CancellationToken ct); }` with `record DeliveryResult(bool Success, int? StatusCode, string? BodySnippet)`.
  - `SignatureService.Sign(string secret, string body)` → `"sha256=" + hex(HMACSHA256(secret, body))`.
  - `BackoffPolicy.NextAttempt(int attempts, DateTime now)` → exponential (e.g. `min(2^attempts, 3600)` seconds), and `MaxAttempts = 8` constant.
  - `DeliveryProcessor.ProcessDueAsync(CancellationToken)` → claims due deliveries (`Pending`/`Failed` with `NextAttemptAt <= now`) using raw SQL `... FOR UPDATE SKIP LOCKED` on Postgres (on SQLite tests, fall back to a plain LINQ claim guarded by a transaction — branch on `Database.IsNpgsql()`), marks `Delivering`, sends with headers `X-Webhook-Id`, `X-Webhook-Event`, `X-Webhook-Signature`; 2xx → `Succeeded`; else `Attempts++`, if `>=MaxAttempts` → `Dead` else `Failed` + `NextAttemptAt=BackoffPolicy.NextAttempt(...)`.
  - `Dispatcher : BackgroundService` — `PeriodicTimer` loop calling `ProcessDueAsync`, try/catch like #2's `RateRefreshService` (registered non-Testing only).
  - `TestWebApplicationFactory.DispatchOnceAsync()` → resolves `DeliveryProcessor` and calls `ProcessDueAsync` (mirrors #2's `RefreshAsync`).
  - Routes (JWT): `GET /v1/deliveries`, `GET /v1/deliveries/{id}`, `POST /v1/deliveries/{id}/replay` (resets `Status=Pending, NextAttemptAt=now`, returns `202`).

- [ ] **Step 1: Write failing tests.** Using `FakeHttpDelivery` (scriptable result + records calls) and `FakeClock`: success path → after `DispatchOnceAsync`, delivery `Succeeded` and the recorded request carries a valid HMAC signature (recompute with `SignatureService` and assert equal); failure path → `Failed`, `Attempts==1`, `NextAttemptAt` in future; advancing clock past max attempts → `Dead`; replay of a `Dead` delivery → re-sent and `Succeeded`.
```csharp
[Fact] public async Task Success_MarksSucceeded_WithValidSignature() { /* ingest, DispatchOnceAsync, assert */ }
[Fact] public async Task Failure_RetriesWithBackoff_ThenDead() { /* fake returns 500; loop+advance clock */ }
[Fact] public async Task Replay_ReQueuesDelivery() { /* ... */ }
```

- [ ] **Step 2: Run to verify fail.** `dotnet test --filter DispatcherTests`. Expected: FAIL.

- [ ] **Step 3: Implement** all pieces. Register `IHttpDelivery`→`HttpDelivery` (non-Testing) and `FakeHttpDelivery` (Testing, via factory), `DeliveryProcessor` (scoped), `Dispatcher` (non-Testing). Add `DispatchOnceAsync` to the factory.

- [ ] **Step 4: Run to verify pass.** Expected: PASS.

- [ ] **Step 5: Commit.** `git commit -am "feat: dispatcher (claim/sign/deliver/retry/backoff/dead) + replay"`

---

### Task 8: Quotas + Stripe billing

**Files:**
- Create: `src/WebhookRelay.Core/Abstractions/{IPlanCatalog.cs,IStripeGateway.cs}`, `src/WebhookRelay.Core/Services/PlanCatalog.cs`, `src/WebhookRelay.Infrastructure/StripeGateway.cs`, `src/WebhookRelay.API/Endpoints/Billing.cs`, `tests/WebhookRelay.Tests/Fakes/FakeStripeGateway.cs`; modify `Events.cs` (event quota), `Endpoints.cs` (endpoint quota); add `Stripe.net` to API `.csproj`.
- Test: `tests/WebhookRelay.Tests/{QuotaTests.cs,BillingTests.cs}`

**Interfaces:**
- Produces:
  - `IPlanCatalog.Limits(Plan)` → `record PlanLimits(int MaxEndpoints, int MaxEventsPerDay)`; `PlanCatalog` reads config (`Plans:Free:*`, `Plans:Pro:*`) with the Global Constraints defaults.
  - Quota checks: ingest counts today's events for tenant (`CreatedAt >= today UTC`) vs `MaxEventsPerDay` → `429` + `Retry-After`; endpoint create counts active endpoints vs `MaxEndpoints` → `403`.
  - `IStripeGateway { Task<string> CreateCheckoutSessionAsync(Tenant t, string priceId); Event ConstructEvent(string json, string sigHeader, string secret); }` (real impl wraps Stripe.net `SessionService` + `EventUtility.ConstructEvent`; fake returns canned data and validates a test signature).
  - Routes: `POST /v1/billing/checkout` (JWT → `200 {url}`), `POST /v1/billing/webhook` (no auth; verify signature via gateway → on `checkout.session.completed`/`customer.subscription.updated` set `Plan=Pro`+status, on `customer.subscription.deleted` set `Plan=Free`; **idempotent** — track processed Stripe event ids, ignore dupes; bad signature → `400`), `GET /v1/billing` (JWT → plan, status, today's usage vs limits).

- [ ] **Step 1: Write failing tests.** `QuotaTests`: with Free limits lowered via test config to 1, second endpoint → 403; 2nd event in a day → 429. `BillingTests`: webhook bad signature → 400; valid `subscription.updated` → tenant Plan becomes Pro; replaying same event id → still Pro, no error (idempotent); `GET /v1/billing` reflects plan + usage.

- [ ] **Step 2: Run to verify fail.** `dotnet test --filter "QuotaTests|BillingTests"`. Expected: FAIL.

- [ ] **Step 3: Implement** PlanCatalog, quota guards in Events/Endpoints, StripeGateway + FakeStripeGateway, Billing.cs, processed-event-id tracking (a small `StripeEvent` table or a `HashSet` persisted row). Register `IStripeGateway` real (non-Testing) / fake (Testing), `IPlanCatalog` always. Add Stripe config keys.

- [ ] **Step 4: Run to verify pass.** Expected: PASS.

- [ ] **Step 5: Commit.** `git commit -am "feat: plan quotas + Stripe checkout/webhook billing"`

---

### Task 9: Deployment + docs

**Files:**
- Create: `Dockerfile`, `render.yaml`, `.github/workflows/ci.yml`, `.github/workflows/keepalive.yml`, `README.md`, `LICENSE`
- Reference: copy each from `fx-rates-api`, rename `FxRates`→`WebhookRelay`/`fx-rates`→`webhook-relay`.

**Interfaces:** Produces deployable artifacts; CI green; Scalar at `/scalar`.

- [ ] **Step 1: Copy + rename Dockerfile** (multi-stage, copy each `.csproj` path, publish API). Verify local build: `docker build -t webhook-relay .` (if Docker available) or `dotnet publish -c Release src/WebhookRelay.API`.

- [ ] **Step 2: Copy + edit render.yaml** — name `webhook-relay-api`, `healthCheckPath: /health`, env vars: `DATABASE_URL` (`sync:false`), `Jwt__Key` (`generateValue:true`), `Stripe__SecretKey`/`Stripe__WebhookSecret`/`Stripe__PriceId` (`sync:false`).

- [ ] **Step 3: Copy CI + keepalive workflows.** Set branch to the repo's default (`main`). Keepalive reads `vars.HEALTHCHECK_URL`, pings `/health` every `*/12`.

- [ ] **Step 4: Write README** (what it is, architecture, endpoint table, how-to-run, how it works: tenancy/queue/signing/billing) + `LICENSE` (MIT, copyright `suzerai1302`). Add `BearerSecuritySchemeTransformer` to API + wire in Program.cs for Scalar Authorize button (copy from #2) if not already done in Task 3.

- [ ] **Step 5: Run full suite + build.** `dotnet test` (all green), `dotnet build -c Release`.

- [ ] **Step 6: Commit.** `git commit -am "chore: Docker, render.yaml, CI, keepalive, README, LICENSE"`

---

## Post-implementation (manual, with user)

- Create GitHub repo under `suzerai1302`, push, confirm CI green.
- User creates Neon DB + Render Docker web service, sets env vars (incl. Stripe test keys + a Pro `price_…`, and a webhook endpoint secret), Manual Deploy.
- Set `HEALTHCHECK_URL` repo variable.
- `verify` live: `/health` 200, register/login, create key+endpoint, ingest event → see delivery succeed against a real test receiver (e.g. webhook.site), Stripe test checkout flips plan, Scalar over https.

## Self-Review

- **Spec coverage:** two-plane auth (T3/T4), tenant isolation filter (T2), entities (T2), ingest+idempotency (T6), dispatcher skip-locked+HMAC+backoff+dead+replay (T7), quotas (T8), Stripe checkout+signed idempotent webhook+billing GET (T8), API surface (T3–T8), deploy+X-Forwarded-Proto+Scalar+CI+keepalive (T1/T9), git identity (Global Constraints). ✓ All spec sections mapped.
- **Placeholders:** test bodies in later tasks are described with intent + signatures rather than full code to keep the plan scannable; the engineer writes assertions against the exact interfaces in each task's **Interfaces** block. Acceptable given the heavy reuse from `fx-rates-api`.
- **Type consistency:** `ProcessDueAsync`, `DispatchOnceAsync`, `DeliveryResult`, `PlanLimits`, `Plan`/`DeliveryStatus` enums used consistently across T2/T7/T8.
