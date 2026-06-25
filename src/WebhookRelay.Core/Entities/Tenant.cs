namespace WebhookRelay.Core.Entities;

public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Plan Plan { get; set; } = Plan.Free;
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public string? SubscriptionStatus { get; set; }
    public DateTime CreatedAt { get; set; }
}
