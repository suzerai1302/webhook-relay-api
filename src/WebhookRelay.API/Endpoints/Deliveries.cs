using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using WebhookRelay.Core.Abstractions;
using WebhookRelay.Core.Entities;
using WebhookRelay.Infrastructure;

namespace WebhookRelay.API.Endpoints;

public static class DeliveriesEndpoints
{
    public static void MapDeliveriesEndpoints(this IEndpointRouteBuilder app)
    {
        // Control plane: all routes are JWT + tenant-scoped via the global query filter.
        var group = app.MapGroup("/v1/deliveries").RequireAuthorization(
            new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
            {
                AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme
            });

        group.MapGet("", async (WebhookRelayDbContext db, CancellationToken ct) =>
        {
            var rows = await db.Deliveries
                .OrderByDescending(d => d.CreatedAt)
                .Select(d => Dto(d))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        group.MapGet("/{id:guid}", async (Guid id, WebhookRelayDbContext db, CancellationToken ct) =>
        {
            var d = await db.Deliveries.FirstOrDefaultAsync(x => x.Id == id, ct);
            return d is null ? Results.NotFound() : Results.Ok(Dto(d));
        });

        // Re-queue a delivery (typically a Dead one after the endpoint recovers): reset to
        // Pending and make it immediately due so the next dispatch pass picks it up.
        group.MapPost("/{id:guid}/replay", async (
            Guid id, WebhookRelayDbContext db, IClock clock, CancellationToken ct) =>
        {
            var d = await db.Deliveries.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return Results.NotFound();

            d.Status = DeliveryStatus.Pending;
            d.NextAttemptAt = clock.UtcNow;
            d.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/v1/deliveries/{d.Id}");
        });
    }

    private static object Dto(Delivery d) => new
    {
        id = d.Id, eventId = d.EventId, endpointId = d.EndpointId,
        status = d.Status.ToString(), attempts = d.Attempts,
        nextAttemptAt = d.NextAttemptAt, lastStatusCode = d.LastStatusCode,
        createdAt = d.CreatedAt, updatedAt = d.UpdatedAt,
    };
}
