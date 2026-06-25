using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookRelay.Core.Abstractions;
using WebhookRelay.Infrastructure;

namespace WebhookRelay.API;

/// <summary>
/// Data-plane authentication. Reads the raw key from <c>X-API-Key</c> or
/// <c>Authorization: Bearer whk_...</c>, hashes it, and resolves the owning (non-revoked)
/// tenant — populating <see cref="ITenantContext"/> so the DbContext query filter applies.
/// </summary>
public class ApiKeyAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";

    private readonly WebhookRelayDbContext _db;
    private readonly IApiKeyHasher _hasher;
    private readonly ITenantContext _tenant;
    private readonly IClock _clock;

    public ApiKeyAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        WebhookRelayDbContext db, IApiKeyHasher hasher, ITenantContext tenant, IClock clock)
        : base(options, logger, encoder)
    {
        _db = db;
        _hasher = hasher;
        _tenant = tenant;
        _clock = clock;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = ExtractKey();
        if (string.IsNullOrEmpty(presented))
            return AuthenticateResult.NoResult();

        // No tenant resolved yet → look up the hash across all tenants.
        var hash = _hasher.Hash(presented);
        var key = await _db.ApiKeys.IgnoreQueryFilters()
            .FirstOrDefaultAsync(k => k.Hash == hash && k.RevokedAt == null);
        if (key is null)
            return AuthenticateResult.Fail("Invalid API key.");

        key.LastUsedAt = _clock.UtcNow;
        await _db.SaveChangesAsync();

        _tenant.TenantId = key.TenantId;

        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim("tenant", key.TenantId.ToString()));
        identity.AddClaim(new Claim("key", key.Id.ToString()));
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private string? ExtractKey()
    {
        if (Request.Headers.TryGetValue("X-API-Key", out var header) && !string.IsNullOrEmpty(header))
            return header.ToString();

        var auth = Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (auth.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            var token = auth[bearer.Length..].Trim();
            if (token.StartsWith("whk_", StringComparison.Ordinal))
                return token;
        }
        return null;
    }
}
