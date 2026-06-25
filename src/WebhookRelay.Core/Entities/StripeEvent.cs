namespace WebhookRelay.Core.Entities;

// Ledger of processed Stripe webhook event ids, so re-delivered events are no-ops (idempotency).
// Global (not tenant-scoped): no query filter.
public class StripeEvent
{
    public string Id { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
}
