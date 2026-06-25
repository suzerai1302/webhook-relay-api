using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WebhookRelay.Tests;

public class QuotaTests
{
    // A tenant's JWT client plus an API-key client sharing the same tenant.
    private static async Task<(HttpClient jwt, HttpClient api)> Tenant(TestWebApplicationFactory f, string email)
    {
        var jwt = f.CreateClient();
        var reg = await jwt.PostAsJsonAsync("/auth/register", new { email, password = "hunter2!" });
        reg.EnsureSuccessStatusCode();
        var token = (await reg.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        jwt.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var keyDoc = await (await jwt.PostAsJsonAsync("/v1/keys", new { label = "quota" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var api = f.CreateClient();
        api.DefaultRequestHeaders.Add("X-API-Key", keyDoc.GetProperty("secret").GetString());
        return (jwt, api);
    }

    [Fact]
    public async Task EndpointQuota_Free_SecondEndpoint_Returns403()
    {
        await using var f = new TestWebApplicationFactory();
        f.ConfigOverrides["Plans:Free:MaxEndpoints"] = "1";
        var (jwt, _) = await Tenant(f, "endpoint-quota@example.com");

        var first = await jwt.PostAsJsonAsync("/v1/endpoints", new { url = "https://one.example.com" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await jwt.PostAsJsonAsync("/v1/endpoints", new { url = "https://two.example.com" });
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
    }

    [Fact]
    public async Task EventQuota_Free_SecondEventSameDay_Returns429WithRetryAfter()
    {
        await using var f = new TestWebApplicationFactory();
        f.ConfigOverrides["Plans:Free:MaxEventsPerDay"] = "1";
        var (jwt, api) = await Tenant(f, "event-quota@example.com");
        await jwt.PostAsJsonAsync("/v1/endpoints", new { url = "https://hook.example.com" });

        var first = await api.PostAsJsonAsync("/v1/events", new { type = "order.created", payload = "{}" });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var second = await api.PostAsJsonAsync("/v1/events", new { type = "order.created", payload = "{}" });
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.True(second.Headers.RetryAfter is not null, "429 must carry a Retry-After header.");
    }
}
