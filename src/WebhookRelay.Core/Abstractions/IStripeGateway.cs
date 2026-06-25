using WebhookRelay.Core.Entities;

namespace WebhookRelay.Core.Abstractions;

// A provider-agnostic view of the Stripe webhook events we act on. Keeps Stripe.net types
// out of Core; the real gateway maps Stripe's Event onto this.
public record StripeWebhookEvent(
    string Id,
    string Type,
    string? TenantRef,
    string? CustomerId,
    string? SubscriptionId,
    string? Status);

public interface IStripeGateway
{
    // Starts a subscription checkout for the tenant; returns the hosted checkout URL.
    Task<string> CreateCheckoutSessionAsync(Tenant tenant, string priceId, CancellationToken ct);

    // Verifies the signature and parses the payload. Throws if the signature is invalid.
    StripeWebhookEvent ConstructEvent(string json, string signatureHeader, string webhookSecret);
}
