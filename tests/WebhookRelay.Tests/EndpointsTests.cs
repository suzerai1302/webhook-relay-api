using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WebhookRelay.Tests;

public class EndpointsTests
{
    private static async Task<HttpClient> AuthedClient(TestWebApplicationFactory f, string email)
    {
        var c = f.CreateClient();
        var res = await c.PostAsJsonAsync("/auth/register", new { email, password = "hunter2!" });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return c;
    }

    [Fact]
    public async Task CreateEndpoint_Returns201_WithSigningSecret()
    {
        await using var f = new TestWebApplicationFactory();
        var c = await AuthedClient(f, "ep-create@example.com");

        var res = await c.PostAsJsonAsync("/v1/endpoints", new { url = "https://example.com/hook" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(Guid.TryParse(doc.GetProperty("id").GetString(), out _));
        Assert.Equal("https://example.com/hook", doc.GetProperty("url").GetString());
        Assert.True(doc.GetProperty("isActive").GetBoolean());
        Assert.False(string.IsNullOrEmpty(doc.GetProperty("signingSecret").GetString()));
    }

    [Fact]
    public async Task ListEndpoints_ScopedToTenant()
    {
        await using var f = new TestWebApplicationFactory();
        var a = await AuthedClient(f, "ep-a@example.com");
        var b = await AuthedClient(f, "ep-b@example.com");
        await a.PostAsJsonAsync("/v1/endpoints", new { url = "https://a.example.com" });
        await b.PostAsJsonAsync("/v1/endpoints", new { url = "https://b1.example.com" });
        await b.PostAsJsonAsync("/v1/endpoints", new { url = "https://b2.example.com" });

        var aList = await (await a.GetAsync("/v1/endpoints")).Content.ReadFromJsonAsync<JsonElement>();
        var bList = await (await b.GetAsync("/v1/endpoints")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, aList.GetArrayLength());
        Assert.Equal(2, bList.GetArrayLength());
        Assert.Equal("https://a.example.com", aList[0].GetProperty("url").GetString());
    }

    private static async Task<string> CreateEndpoint(HttpClient c, string url)
    {
        var doc = await (await c.PostAsJsonAsync("/v1/endpoints", new { url }))
            .Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task PatchEndpoint_TogglesIsActive_AndUpdatesUrl()
    {
        await using var f = new TestWebApplicationFactory();
        var c = await AuthedClient(f, "ep-patch@example.com");
        var id = await CreateEndpoint(c, "https://old.example.com");

        var res = await c.PatchAsJsonAsync($"/v1/endpoints/{id}",
            new { isActive = false, url = "https://new.example.com" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(doc.GetProperty("isActive").GetBoolean());
        Assert.Equal("https://new.example.com", doc.GetProperty("url").GetString());
    }

    [Fact]
    public async Task PatchEndpoint_OtherTenant_Returns404()
    {
        await using var f = new TestWebApplicationFactory();
        var a = await AuthedClient(f, "ep-patch-a@example.com");
        var b = await AuthedClient(f, "ep-patch-b@example.com");
        var id = await CreateEndpoint(a, "https://a.example.com");

        var res = await b.PatchAsJsonAsync($"/v1/endpoints/{id}", new { isActive = false });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task DeleteEndpoint_Returns204_Then404()
    {
        await using var f = new TestWebApplicationFactory();
        var c = await AuthedClient(f, "ep-del@example.com");
        var id = await CreateEndpoint(c, "https://del.example.com");

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/v1/endpoints/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/v1/endpoints/{id}")).StatusCode);
        var list = await (await c.GetAsync("/v1/endpoints")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, list.GetArrayLength());
    }
}
