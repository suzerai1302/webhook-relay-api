using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WebhookRelay.Tests;

public class IngestTests
{
    // A tenant's JWT client plus an API-key client sharing the same tenant.
    private static async Task<(HttpClient jwt, HttpClient api)> Tenant(TestWebApplicationFactory f, string email)
    {
        var jwt = f.CreateClient();
        var reg = await jwt.PostAsJsonAsync("/auth/register", new { email, password = "hunter2!" });
        reg.EnsureSuccessStatusCode();
        var token = (await reg.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        jwt.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var keyDoc = await (await jwt.PostAsJsonAsync("/v1/keys", new { label = "ingest" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var api = f.CreateClient();
        api.DefaultRequestHeaders.Add("X-API-Key", keyDoc.GetProperty("secret").GetString());
        return (jwt, api);
    }

    private static Task AddEndpoint(HttpClient jwt, string url, string? eventTypeFilter = null) =>
        jwt.PostAsJsonAsync("/v1/endpoints", new { url, eventTypeFilter });

    [Fact]
    public async Task Ingest_CreatesDeliveryPerActiveEndpoint_Returns202()
    {
        await using var f = new TestWebApplicationFactory();
        var (jwt, api) = await Tenant(f, "ingest@example.com");
        await AddEndpoint(jwt, "https://one.example.com");
        await AddEndpoint(jwt, "https://two.example.com");

        var res = await api.PostAsJsonAsync("/v1/events", new { type = "order.created", payload = "{}" });
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        var eventId = doc.GetProperty("eventId").GetString();
        Assert.True(Guid.TryParse(eventId, out _));

        // The JWT side can read the event back with one delivery per endpoint.
        var view = await (await jwt.GetAsync($"/v1/events/{eventId}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, view.GetProperty("deliveries").GetArrayLength());
    }

    [Fact]
    public async Task Ingest_RespectsEventTypeFilter()
    {
        await using var f = new TestWebApplicationFactory();
        var (jwt, api) = await Tenant(f, "filter@example.com");
        await AddEndpoint(jwt, "https://all.example.com");                            // no filter → matches
        await AddEndpoint(jwt, "https://orders.example.com", "order.created");        // matches
        await AddEndpoint(jwt, "https://refunds.example.com", "refund.created");      // filtered out

        var res = await api.PostAsJsonAsync("/v1/events", new { type = "order.created", payload = "{}" });
        var eventId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetString();

        var view = await (await jwt.GetAsync($"/v1/events/{eventId}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, view.GetProperty("deliveries").GetArrayLength());
    }

    [Fact]
    public async Task Ingest_SkipsInactiveEndpoints()
    {
        await using var f = new TestWebApplicationFactory();
        var (jwt, api) = await Tenant(f, "inactive@example.com");
        var created = await (await jwt.PostAsJsonAsync("/v1/endpoints", new { url = "https://off.example.com" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        await jwt.PatchAsJsonAsync($"/v1/endpoints/{created.GetProperty("id").GetString()}", new { isActive = false });

        var res = await api.PostAsJsonAsync("/v1/events", new { type = "x", payload = "{}" });
        var eventId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetString();

        var view = await (await jwt.GetAsync($"/v1/events/{eventId}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, view.GetProperty("deliveries").GetArrayLength());
    }

    [Fact]
    public async Task Ingest_WithoutApiKey_Returns401()
    {
        await using var f = new TestWebApplicationFactory();
        var res = await f.CreateClient().PostAsJsonAsync("/v1/events", new { type = "x", payload = "{}" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Ingest_SameIdempotencyKey_DedupesToSameEvent()
    {
        await using var f = new TestWebApplicationFactory();
        var (jwt, api) = await Tenant(f, "idem@example.com");
        await AddEndpoint(jwt, "https://hook.example.com");

        async Task<string> Post()
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
            {
                Content = JsonContent.Create(new { type = "order.created", payload = "{}" }),
            };
            msg.Headers.Add("Idempotency-Key", "abc-123");
            var r = await api.SendAsync(msg);
            return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetString()!;
        }

        var first = await Post();
        var second = await Post();
        Assert.Equal(first, second);

        // The replay must not create a second delivery.
        var view = await (await jwt.GetAsync($"/v1/events/{first}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, view.GetProperty("deliveries").GetArrayLength());
    }
}
