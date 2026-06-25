using WebhookRelay.Core.Abstractions;

namespace WebhookRelay.API;

/// <summary>Scoped per request; populated by auth (JWT tenant claim or API key lookup).</summary>
public class TenantContext : ITenantContext
{
    public Guid? TenantId { get; set; }
}
