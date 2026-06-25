using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using WebhookRelay.Core.Abstractions;
using WebhookRelay.Core.Entities;
using WebhookRelay.Infrastructure;

namespace WebhookRelay.API.Endpoints;

public static class BillingEndpoints
{
    public static void MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        // Control plane: current plan, subscription status, and today's usage vs limits.
        app.MapGet("/v1/billing", async (
            WebhookRelayDbContext db, ITenantContext tenant, IClock clock,
            IPlanCatalog plans, CancellationToken ct) =>
        {
            var tenantId = tenant.TenantId!.Value;
            var t = (await db.Tenants.FindAsync([tenantId], ct))!;
            var limits = plans.Limits(t.Plan);

            var dayStart = clock.UtcNow.Date;
            var endpointsUsed = await db.Endpoints.CountAsync(e => e.IsActive, ct);
            var eventsToday = await db.Events.CountAsync(e => e.CreatedAt >= dayStart, ct);

            return Results.Ok(new
            {
                tenantId,
                plan = t.Plan.ToString(),
                subscriptionStatus = t.SubscriptionStatus,
                limits = new { maxEndpoints = limits.MaxEndpoints, maxEventsPerDay = limits.MaxEventsPerDay },
                usage = new { endpoints = endpointsUsed, eventsToday },
            });
        }).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
        {
            AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme
        });

        // Control plane: start a Stripe Checkout session to upgrade to Pro.
        app.MapPost("/v1/billing/checkout", async (
            WebhookRelayDbContext db, ITenantContext tenant, IStripeGateway stripe,
            IConfiguration config, CancellationToken ct) =>
        {
            var t = (await db.Tenants.FindAsync([tenant.TenantId!.Value], ct))!;
            var priceId = config["Stripe:PriceId"];
            if (string.IsNullOrEmpty(priceId))
                return Results.Problem(statusCode: 500, title: "Billing is not configured (missing price id).");

            var url = await stripe.CreateCheckoutSessionAsync(t, priceId, ct);
            return Results.Ok(new { url });
        }).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
        {
            AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme
        });

        // Stripe webhook (no auth — verified by signature). Idempotent on the Stripe event id.
        app.MapPost("/v1/billing/webhook", async (
            HttpRequest http, WebhookRelayDbContext db, IStripeGateway stripe,
            IConfiguration config, IClock clock, CancellationToken ct) =>
        {
            using var reader = new StreamReader(http.Body);
            var json = await reader.ReadToEndAsync(ct);
            var signature = http.Headers["Stripe-Signature"].ToString();
            var secret = config["Stripe:WebhookSecret"] ?? "";

            StripeWebhookEvent ev;
            try
            {
                ev = stripe.ConstructEvent(json, signature, secret);
            }
            catch
            {
                return Results.BadRequest(new { error = "Invalid Stripe signature." });
            }

            // Idempotency: a re-delivered event id is a no-op.
            if (await db.StripeEvents.AnyAsync(s => s.Id == ev.Id, ct))
                return Results.Ok(new { status = "duplicate" });

            // Resolve the tenant: checkout carries our client_reference_id; subscription events
            // only carry the Stripe customer id, so fall back to that.
            var tenant = Guid.TryParse(ev.TenantRef, out var tid)
                ? await db.Tenants.FirstOrDefaultAsync(t => t.Id == tid, ct)
                : null;
            tenant ??= ev.CustomerId is null
                ? null
                : await db.Tenants.FirstOrDefaultAsync(t => t.StripeCustomerId == ev.CustomerId, ct);

            if (tenant is not null)
            {
                switch (ev.Type)
                {
                    case "checkout.session.completed":
                    case "customer.subscription.updated":
                        tenant.Plan = Plan.Pro;
                        tenant.SubscriptionStatus = ev.Status ?? "active";
                        if (ev.CustomerId is not null) tenant.StripeCustomerId = ev.CustomerId;
                        if (ev.SubscriptionId is not null) tenant.StripeSubscriptionId = ev.SubscriptionId;
                        break;
                    case "customer.subscription.deleted":
                        tenant.Plan = Plan.Free;
                        tenant.SubscriptionStatus = ev.Status ?? "canceled";
                        break;
                }
            }

            db.StripeEvents.Add(new StripeEvent { Id = ev.Id, ReceivedAt = clock.UtcNow });
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });
    }
}
