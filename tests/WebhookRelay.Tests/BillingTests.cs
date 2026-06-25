using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace WebhookRelay.Tests;

public class BillingTests
{
    private static async Task<HttpClient> Tenant(TestWebApplicationFactory f, string email)
    {
        var jwt = f.CreateClient();
        var reg = await jwt.PostAsJsonAsync("/auth/register", new { email, password = "hunter2!" });
        reg.EnsureSuccessStatusCode();
        var token = (await reg.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        jwt.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return jwt;
    }

    [Fact]
    public async Task GetBilling_NewTenant_ReportsFreePlanAndUsage()
    {
        await using var f = new TestWebApplicationFactory();
        var jwt = await Tenant(f, "billing-view@example.com");

        var doc = await (await jwt.GetAsync("/v1/billing")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Free", doc.GetProperty("plan").GetString());
        Assert.Equal(2, doc.GetProperty("limits").GetProperty("maxEndpoints").GetInt32());
        Assert.Equal(0, doc.GetProperty("usage").GetProperty("endpoints").GetInt32());
    }

    // Posts a raw webhook body with the given Stripe-Signature (the fake treats "valid" as verified).
    private static Task<HttpResponseMessage> PostWebhook(HttpClient client, object payload, string signature)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/billing/webhook")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Add("Stripe-Signature", signature);
        return client.SendAsync(msg);
    }

    [Fact]
    public async Task Webhook_BadSignature_Returns400()
    {
        await using var f = new TestWebApplicationFactory();
        var anon = f.CreateClient();

        var res = await PostWebhook(anon,
            new { id = "evt_1", type = "checkout.session.completed" }, signature: "tampered");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    private static async Task<(HttpClient jwt, string tenantId)> TenantWithId(
        TestWebApplicationFactory f, string email)
    {
        var jwt = await Tenant(f, email);
        var doc = await (await jwt.GetAsync("/v1/billing")).Content.ReadFromJsonAsync<JsonElement>();
        return (jwt, doc.GetProperty("tenantId").GetString()!);
    }

    [Fact]
    public async Task Webhook_CheckoutCompleted_UpgradesTenantToPro()
    {
        await using var f = new TestWebApplicationFactory();
        var (jwt, tenantId) = await TenantWithId(f, "upgrade@example.com");

        var res = await PostWebhook(f.CreateClient(), new
        {
            id = "evt_upgrade",
            type = "checkout.session.completed",
            tenantRef = tenantId,
            customerId = "cus_123",
            subscriptionId = "sub_123",
            status = "active",
        }, signature: "valid");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var doc = await (await jwt.GetAsync("/v1/billing")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Pro", doc.GetProperty("plan").GetString());
        Assert.Equal(20, doc.GetProperty("limits").GetProperty("maxEndpoints").GetInt32());
    }

    [Fact]
    public async Task Webhook_ReplayedEventId_IsIgnored()
    {
        await using var f = new TestWebApplicationFactory();
        var (jwt, tenantId) = await TenantWithId(f, "idempotent@example.com");
        var anon = f.CreateClient();

        // First delivery upgrades to Pro.
        await PostWebhook(anon, new
        {
            id = "evt_same", type = "checkout.session.completed",
            tenantRef = tenantId, customerId = "cus_9", subscriptionId = "sub_9", status = "active",
        }, signature: "valid");

        // A re-delivery reusing the SAME event id — even carrying a cancellation — must be a no-op.
        var replay = await PostWebhook(anon, new
        {
            id = "evt_same", type = "customer.subscription.deleted",
            tenantRef = tenantId, customerId = "cus_9", status = "canceled",
        }, signature: "valid");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        var doc = await (await jwt.GetAsync("/v1/billing")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Pro", doc.GetProperty("plan").GetString());
    }

    [Fact]
    public async Task Checkout_ReturnsHostedUrl()
    {
        await using var f = new TestWebApplicationFactory();
        f.Stripe.CheckoutUrl = "https://checkout.stripe.test/session/xyz";
        f.ConfigOverrides["Stripe:PriceId"] = "price_test";
        var jwt = await Tenant(f, "checkout@example.com");

        var res = await jwt.PostAsync("/v1/billing/checkout", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://checkout.stripe.test/session/xyz", doc.GetProperty("url").GetString());
    }
}
