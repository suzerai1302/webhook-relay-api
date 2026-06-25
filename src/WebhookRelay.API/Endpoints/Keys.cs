using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using WebhookRelay.Core.Abstractions;
using WebhookRelay.Core.Entities;
using WebhookRelay.Infrastructure;

namespace WebhookRelay.API.Endpoints;

public record CreateKeyRequest(string Label);

public static class KeysEndpoints
{
    public static void MapKeysEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/keys")
            .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
            {
                AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme
            });

        group.MapPost("", async (
            CreateKeyRequest req, WebhookRelayDbContext db, IApiKeyHasher hasher,
            ITenantContext tenant, IClock clock, CancellationToken ct) =>
        {
            // 32 random bytes, url-safe; prefixed so receivers can recognize our keys.
            var secret = "whk_live_" + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var key = new ApiKey
            {
                TenantId = tenant.TenantId!.Value,
                Hash = hasher.Hash(secret),
                Prefix = secret[..12],
                Label = req.Label ?? "",
                CreatedAt = clock.UtcNow,
            };
            db.ApiKeys.Add(key);
            await db.SaveChangesAsync(ct);

            // The secret is shown exactly once — only its hash is persisted.
            return Results.Created($"/v1/keys/{key.Id}", new { id = key.Id, prefix = key.Prefix, secret });
        });

        group.MapGet("", async (WebhookRelayDbContext db, CancellationToken ct) =>
        {
            // Tenant-scoped by the query filter. Never project the hash or secret.
            var keys = await db.ApiKeys
                .OrderByDescending(k => k.CreatedAt)
                .Select(k => new
                {
                    k.Id, k.Prefix, k.Label, k.LastUsedAt, k.RevokedAt, k.CreatedAt,
                })
                .ToListAsync(ct);
            return Results.Ok(keys);
        });

        group.MapDelete("/{id:guid}", async (
            Guid id, WebhookRelayDbContext db, IClock clock, CancellationToken ct) =>
        {
            // Tenant-scoped by the query filter, so one tenant can't revoke another's key.
            var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
            if (key is null) return Results.NotFound();
            key.RevokedAt ??= clock.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
