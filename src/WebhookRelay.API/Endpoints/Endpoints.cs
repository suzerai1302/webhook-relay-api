using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using WebhookRelay.Core.Abstractions;
using WebhookRelay.Core.Entities;
using WebhookRelay.Infrastructure;
using Endpoint = WebhookRelay.Core.Entities.Endpoint;

namespace WebhookRelay.API.Endpoints;

public record CreateEndpointRequest(string Url, string? EventTypeFilter);
public record PatchEndpointRequest(string? Url, bool? IsActive, string? EventTypeFilter);

public static class EndpointsEndpoints
{
    public static void MapEndpointsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/endpoints")
            .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
            {
                AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme
            });

        group.MapPost("", async (
            CreateEndpointRequest req, WebhookRelayDbContext db,
            ITenantContext tenant, IClock clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Url))
                return Results.Problem(statusCode: 400, title: "url is required.");

            var endpoint = new Endpoint
            {
                TenantId = tenant.TenantId!.Value,
                Url = req.Url,
                // Per-endpoint secret used to HMAC-sign deliveries (see dispatcher).
                SigningSecret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
                IsActive = true,
                EventTypeFilter = string.IsNullOrWhiteSpace(req.EventTypeFilter) ? null : req.EventTypeFilter,
                CreatedAt = clock.UtcNow,
            };
            db.Endpoints.Add(endpoint);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/v1/endpoints/{endpoint.Id}", Dto(endpoint));
        });

        group.MapGet("", async (WebhookRelayDbContext db, CancellationToken ct) =>
        {
            var rows = await db.Endpoints.OrderByDescending(e => e.CreatedAt).ToListAsync(ct);
            return Results.Ok(rows.Select(Dto));
        });

        group.MapPatch("/{id:guid}", async (
            Guid id, PatchEndpointRequest req, WebhookRelayDbContext db, CancellationToken ct) =>
        {
            var endpoint = await db.Endpoints.FirstOrDefaultAsync(e => e.Id == id, ct);
            if (endpoint is null) return Results.NotFound();

            if (!string.IsNullOrWhiteSpace(req.Url)) endpoint.Url = req.Url;
            if (req.IsActive is { } active) endpoint.IsActive = active;
            // EventTypeFilter is nullable; an explicit empty string clears it.
            if (req.EventTypeFilter is not null)
                endpoint.EventTypeFilter = req.EventTypeFilter.Length == 0 ? null : req.EventTypeFilter;

            await db.SaveChangesAsync(ct);
            return Results.Ok(Dto(endpoint));
        });

        group.MapDelete("/{id:guid}", async (Guid id, WebhookRelayDbContext db, CancellationToken ct) =>
        {
            var endpoint = await db.Endpoints.FirstOrDefaultAsync(e => e.Id == id, ct);
            if (endpoint is null) return Results.NotFound();
            db.Endpoints.Remove(endpoint);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static object Dto(Endpoint e) => new
    {
        id = e.Id, url = e.Url, signingSecret = e.SigningSecret,
        isActive = e.IsActive, eventTypeFilter = e.EventTypeFilter, createdAt = e.CreatedAt,
    };
}
