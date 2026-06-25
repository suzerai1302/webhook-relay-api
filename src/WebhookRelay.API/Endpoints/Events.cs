using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using WebhookRelay.Core.Abstractions;
using WebhookRelay.Core.Entities;
using WebhookRelay.Infrastructure;

namespace WebhookRelay.API.Endpoints;

public record IngestRequest(string Type, string Payload);

public static class EventsEndpoints
{
    public static void MapEventsEndpoints(this IEndpointRouteBuilder app)
    {
        // Data plane: ingest events with an API key.
        app.MapPost("/v1/events", async (
            IngestRequest req, HttpRequest http, WebhookRelayDbContext db,
            ITenantContext tenant, IClock clock, CancellationToken ct) =>
        {
            var now = clock.UtcNow;
            var tenantId = tenant.TenantId!.Value;

            // Idempotent replay: same key for a tenant returns the original event, no new rows.
            var idempotencyKey = http.Headers["Idempotency-Key"].ToString();
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                var existing = await db.Events.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.IdempotencyKey == idempotencyKey, ct);
                if (existing is not null)
                    return Results.Accepted($"/v1/events/{existing.Id}", new { eventId = existing.Id });
            }

            var ev = new Event
            {
                TenantId = tenantId,
                Type = req.Type,
                Payload = req.Payload ?? "",
                IdempotencyKey = string.IsNullOrEmpty(idempotencyKey) ? null : idempotencyKey,
                CreatedAt = now,
            };
            db.Events.Add(ev);

            // Fan out: one Pending delivery per active endpoint whose filter matches the type.
            var endpoints = await db.Endpoints
                .Where(e => e.IsActive && (e.EventTypeFilter == null || e.EventTypeFilter == req.Type))
                .ToListAsync(ct);
            foreach (var endpoint in endpoints)
            {
                db.Deliveries.Add(new Delivery
                {
                    TenantId = ev.TenantId,
                    EventId = ev.Id,
                    EndpointId = endpoint.Id,
                    Status = DeliveryStatus.Pending,
                    NextAttemptAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            await db.SaveChangesAsync(ct);

            return Results.Accepted($"/v1/events/{ev.Id}", new { eventId = ev.Id });
        }).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
        {
            AuthenticationSchemes = ApiKeyAuthHandler.SchemeName
        });

        // Control plane: inspect an event and its deliveries.
        app.MapGet("/v1/events/{id:guid}", async (Guid id, WebhookRelayDbContext db, CancellationToken ct) =>
        {
            var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == id, ct);
            if (ev is null) return Results.NotFound();

            var deliveries = await db.Deliveries.Where(d => d.EventId == id)
                .Select(d => new
                {
                    d.Id, d.EndpointId, status = d.Status.ToString(), d.Attempts,
                    d.NextAttemptAt, d.LastStatusCode,
                })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                id = ev.Id, type = ev.Type, payload = ev.Payload,
                idempotencyKey = ev.IdempotencyKey, createdAt = ev.CreatedAt, deliveries,
            });
        }).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
        {
            AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme
        });
    }
}
