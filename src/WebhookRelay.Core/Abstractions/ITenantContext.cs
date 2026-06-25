namespace WebhookRelay.Core.Abstractions;

/// <summary>Holds the tenant resolved for the current request (from JWT or API key).</summary>
public interface ITenantContext
{
    Guid? TenantId { get; set; }
}
