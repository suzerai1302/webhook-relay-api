using Stripe;
using Stripe.Checkout;
using WebhookRelay.Core.Abstractions;
using Tenant = WebhookRelay.Core.Entities.Tenant;

namespace WebhookRelay.API;

// Real Stripe adapter: wraps Stripe.net's Checkout SessionService + webhook signature
// verification, mapping the parts we care about onto the provider-agnostic StripeWebhookEvent.
public class StripeGateway : IStripeGateway
{
    private readonly IConfiguration _config;

    public StripeGateway(IConfiguration config)
    {
        _config = config;
        StripeConfiguration.ApiKey = config["Stripe:SecretKey"];
    }

    public async Task<string> CreateCheckoutSessionAsync(Tenant tenant, string priceId, CancellationToken ct)
    {
        var options = new SessionCreateOptions
        {
            Mode = "subscription",
            LineItems = [new SessionLineItemOptions { Price = priceId, Quantity = 1 }],
            // Carries the tenant identity back to us on checkout.session.completed.
            ClientReferenceId = tenant.Id.ToString(),
            Customer = tenant.StripeCustomerId,
            // Stripe rejects empty strings, so treat blank config as unset and fall back.
            SuccessUrl = Fallback(_config["Stripe:SuccessUrl"], "https://example.com/billing/success"),
            CancelUrl = Fallback(_config["Stripe:CancelUrl"], "https://example.com/billing/cancel"),
        };
        var session = await new SessionService().CreateAsync(options, cancellationToken: ct);
        return session.Url;
    }

    private static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    public StripeWebhookEvent ConstructEvent(string json, string signatureHeader, string webhookSecret)
    {
        // Throws StripeException if the signature doesn't verify against the secret.
        var ev = EventUtility.ConstructEvent(json, signatureHeader, webhookSecret);

        string? tenantRef = null, customerId = null, subscriptionId = null, status = null;
        switch (ev.Data.Object)
        {
            case Session s:
                tenantRef = s.ClientReferenceId;
                customerId = s.CustomerId;
                subscriptionId = s.SubscriptionId;
                status = "active";
                break;
            case Subscription sub:
                customerId = sub.CustomerId;
                subscriptionId = sub.Id;
                status = sub.Status;
                break;
        }
        return new StripeWebhookEvent(ev.Id, ev.Type, tenantRef, customerId, subscriptionId, status);
    }
}
