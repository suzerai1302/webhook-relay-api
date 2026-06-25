using System.Text.Json;
using WebhookRelay.Core.Abstractions;
using WebhookRelay.Core.Entities;

namespace WebhookRelay.Tests.Fakes;

// Stand-in for Stripe: checkout returns a canned URL, and ConstructEvent treats the literal
// signature "valid" as verified (anything else throws) while parsing the JSON body the test
// posted directly into a StripeWebhookEvent.
public class FakeStripeGateway : IStripeGateway
{
    public string CheckoutUrl { get; set; } = "https://checkout.stripe.test/session/abc";

    public Task<string> CreateCheckoutSessionAsync(Tenant tenant, string priceId, CancellationToken ct)
        => Task.FromResult(CheckoutUrl);

    public StripeWebhookEvent ConstructEvent(string json, string signatureHeader, string webhookSecret)
    {
        if (signatureHeader != "valid")
            throw new InvalidOperationException("Invalid Stripe signature.");
        return JsonSerializer.Deserialize<StripeWebhookEvent>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
