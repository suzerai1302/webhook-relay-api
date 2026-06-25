using Microsoft.EntityFrameworkCore;
using WebhookRelay.Core.Abstractions;
using WebhookRelay.Core.Entities;
using WebhookRelay.Infrastructure;

namespace WebhookRelay.API.Endpoints;

public record RegisterRequest(string Email, string Password);
public record LoginRequest(string Email, string Password);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/register", async (
            RegisterRequest req, WebhookRelayDbContext db, IPasswordHasher hasher,
            ITokenIssuer tokens, IClock clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                return Results.Problem(statusCode: 400, title: "email and password are required.");

            // No tenant resolved during registration → bypass the tenant query filter.
            if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == req.Email, ct))
                return Results.Conflict(new { error = "Email already registered." });

            var now = clock.UtcNow;
            var tenant = new Tenant { Plan = Plan.Free, CreatedAt = now };
            var user = new User
            {
                TenantId = tenant.Id,
                Email = req.Email,
                PasswordHash = hasher.Hash(req.Password),
                CreatedAt = now,
            };
            db.Tenants.Add(tenant);
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/tenants/{tenant.Id}", new { token = tokens.CreateToken(user) });
        });

        app.MapPost("/auth/login", async (
            LoginRequest req, WebhookRelayDbContext db, IPasswordHasher hasher,
            ITokenIssuer tokens, CancellationToken ct) =>
        {
            var user = await db.Users.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Email == req.Email, ct);
            if (user is null || !hasher.Verify(req.Password, user.PasswordHash))
                return Results.Unauthorized();

            return Results.Ok(new { token = tokens.CreateToken(user) });
        });
    }
}
