using System.Net.Http.Json;
using System.Text.Json;
using WebhookRelay.Core.Abstractions;
using WebhookRelay.Core.Services;

namespace WebhookRelay.Tests;

public class DispatcherTests
{
    // A tenant's JWT client, an API-key client, and the signing secret of one endpoint.
    private static async Task<(HttpClient jwt, HttpClient api, string secret)> TenantWithEndpoint(
        TestWebApplicationFactory f, string email, string url)
    {
        var jwt = f.CreateClient();
        var reg = await jwt.PostAsJsonAsync("/auth/register", new { email, password = "hunter2!" });
        reg.EnsureSuccessStatusCode();
        var token = (await reg.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        jwt.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var keyDoc = await (await jwt.PostAsJsonAsync("/v1/keys", new { label = "dispatch" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var api = f.CreateClient();
        api.DefaultRequestHeaders.Add("X-API-Key", keyDoc.GetProperty("secret").GetString());

        var epDoc = await (await jwt.PostAsJsonAsync("/v1/endpoints", new { url }))
            .Content.ReadFromJsonAsync<JsonElement>();
        return (jwt, api, epDoc.GetProperty("signingSecret").GetString()!);
    }

    private static async Task<string> Ingest(HttpClient api, string type, string payload)
    {
        var res = await api.PostAsJsonAsync("/v1/events", new { type, payload });
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetString()!;
    }

    private static async Task<JsonElement> Deliveries(HttpClient jwt, string eventId) =>
        (await (await jwt.GetAsync($"/v1/events/{eventId}")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("deliveries");

    [Fact]
    public async Task Success_MarksSucceeded_WithValidSignature()
    {
        await using var f = new TestWebApplicationFactory();
        var (jwt, api, secret) = await TenantWithEndpoint(f, "ok@example.com", "https://hook.example.com");
        var payload = "{\"hello\":\"world\"}";
        var eventId = await Ingest(api, "order.created", payload);

        await f.DispatchOnceAsync();

        var deliveries = await Deliveries(jwt, eventId);
        Assert.Equal("Succeeded", deliveries[0].GetProperty("status").GetString());

        // The dispatcher signed the body with the endpoint's secret.
        Assert.True(f.Http.Calls.TryDequeue(out var call));
        Assert.Equal(payload, call!.Body);
        Assert.Equal(SignatureService.Sign(secret, payload), call.Headers["X-Webhook-Signature"]);
    }

    [Fact]
    public async Task Failure_MarksFailed_IncrementsAttempts_SchedulesRetry()
    {
        await using var f = new TestWebApplicationFactory();
        var (jwt, api, _) = await TenantWithEndpoint(f, "fail@example.com", "https://down.example.com");
        f.Http.Respond = _ => new DeliveryResult(false, 500, "boom");
        var eventId = await Ingest(api, "order.created", "{}");

        await f.DispatchOnceAsync();

        var d = (await Deliveries(jwt, eventId))[0];
        Assert.Equal("Failed", d.GetProperty("status").GetString());
        Assert.Equal(1, d.GetProperty("attempts").GetInt32());
        Assert.True(d.GetProperty("nextAttemptAt").GetDateTime() > f.Clock.UtcNow);
    }

    [Fact]
    public async Task RepeatedFailure_EventuallyMarksDead()
    {
        await using var f = new TestWebApplicationFactory();
        var (jwt, api, _) = await TenantWithEndpoint(f, "dead@example.com", "https://gone.example.com");
        f.Http.Respond = _ => new DeliveryResult(false, 500, "boom");
        var eventId = await Ingest(api, "order.created", "{}");

        // Dispatch repeatedly, advancing past each scheduled backoff, until attempts exhaust.
        for (var i = 0; i < BackoffPolicy.MaxAttempts; i++)
        {
            await f.DispatchOnceAsync();
            f.Clock.Advance(TimeSpan.FromHours(2)); // beyond the 1h backoff cap
        }

        var d = (await Deliveries(jwt, eventId))[0];
        Assert.Equal("Dead", d.GetProperty("status").GetString());
        Assert.Equal(BackoffPolicy.MaxAttempts, d.GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task Replay_ReQueuesDeadDelivery_ThenSucceeds()
    {
        await using var f = new TestWebApplicationFactory();
        var (jwt, api, _) = await TenantWithEndpoint(f, "replay@example.com", "https://flaky.example.com");
        f.Http.Respond = _ => new DeliveryResult(false, 500, "boom");
        var eventId = await Ingest(api, "order.created", "{}");
        for (var i = 0; i < BackoffPolicy.MaxAttempts; i++)
        {
            await f.DispatchOnceAsync();
            f.Clock.Advance(TimeSpan.FromHours(2));
        }
        var deadId = (await Deliveries(jwt, eventId))[0].GetProperty("id").GetString();

        // The endpoint recovers; operator replays the dead delivery.
        f.Http.Respond = _ => new DeliveryResult(true, 200, "ok");
        var replay = await jwt.PostAsync($"/v1/deliveries/{deadId}/replay", null);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, replay.StatusCode);

        await f.DispatchOnceAsync();

        var d = (await Deliveries(jwt, eventId))[0];
        Assert.Equal("Succeeded", d.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ListDeliveries_ReturnsTenantsDeliveries()
    {
        await using var f = new TestWebApplicationFactory();
        var (jwt, api, _) = await TenantWithEndpoint(f, "list@example.com", "https://hook.example.com");
        await Ingest(api, "order.created", "{}");
        await f.DispatchOnceAsync();

        var list = await (await jwt.GetAsync("/v1/deliveries")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("Succeeded", list[0].GetProperty("status").GetString());
    }
}
