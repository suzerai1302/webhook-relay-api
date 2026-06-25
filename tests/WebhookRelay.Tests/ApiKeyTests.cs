using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WebhookRelay.Tests;

public class ApiKeyTests
{
    private static async Task<string> Register(HttpClient c, string email)
    {
        var res = await c.PostAsJsonAsync("/auth/register", new { email, password = "hunter2!" });
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("token").GetString()!;
    }

    private static HttpClient Authed(TestWebApplicationFactory f, string token)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return c;
    }

    [Fact]
    public async Task CreateKey_ReturnsSecretOnce_AndPrefix()
    {
        await using var f = new TestWebApplicationFactory();
        var token = await Register(f.CreateClient(), "keys@example.com");
        var c = Authed(f, token);

        var res = await c.PostAsJsonAsync("/v1/keys", new { label = "ci" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        var secret = doc.GetProperty("secret").GetString()!;
        var prefix = doc.GetProperty("prefix").GetString()!;
        Assert.True(Guid.TryParse(doc.GetProperty("id").GetString(), out _));
        Assert.StartsWith("whk_live_", secret);
        Assert.StartsWith("whk_live_", prefix);
        Assert.True(secret.Length > prefix.Length);
    }

    [Fact]
    public async Task ListKeys_OmitsSecret_ShowsPrefixAndLabel()
    {
        await using var f = new TestWebApplicationFactory();
        var token = await Register(f.CreateClient(), "list@example.com");
        var c = Authed(f, token);
        await c.PostAsJsonAsync("/v1/keys", new { label = "deploy" });

        var res = await c.GetAsync("/v1/keys");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var arr = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, arr.GetArrayLength());
        var key = arr[0];
        Assert.Equal("deploy", key.GetProperty("label").GetString());
        Assert.StartsWith("whk_live_", key.GetProperty("prefix").GetString());
        Assert.False(key.TryGetProperty("secret", out _));
        Assert.False(key.TryGetProperty("hash", out _));
    }

    [Fact]
    public async Task OtherTenant_CannotSeeKeys()
    {
        await using var f = new TestWebApplicationFactory();
        var ca = Authed(f, await Register(f.CreateClient(), "a-tenant@example.com"));
        var cb = Authed(f, await Register(f.CreateClient(), "b-tenant@example.com"));
        await ca.PostAsJsonAsync("/v1/keys", new { label = "a-only" });

        var res = await cb.GetAsync("/v1/keys");
        var arr = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, arr.GetArrayLength());
    }

    [Fact]
    public async Task DeleteKey_Revokes_AndShowsRevokedAtInList()
    {
        await using var f = new TestWebApplicationFactory();
        var c = Authed(f, await Register(f.CreateClient(), "revoke@example.com"));
        var created = await (await c.PostAsJsonAsync("/v1/keys", new { label = "x" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();

        var del = await c.DeleteAsync($"/v1/keys/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var arr = await (await c.GetAsync("/v1/keys")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(JsonValueKind.Null, arr[0].GetProperty("revokedAt").ValueKind);
    }

    [Fact]
    public async Task DeleteKey_UnknownId_Returns404()
    {
        await using var f = new TestWebApplicationFactory();
        var c = Authed(f, await Register(f.CreateClient(), "missing@example.com"));
        var del = await c.DeleteAsync($"/v1/keys/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
    }

    // Creates a key and returns its one-time secret.
    private static async Task<string> CreateKey(HttpClient jwtClient)
    {
        var doc = await (await jwtClient.PostAsJsonAsync("/v1/keys", new { label = "dp" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("secret").GetString()!;
    }

    [Fact]
    public async Task ApiKey_ResolvesTenant_OnDataPlane()
    {
        await using var f = new TestWebApplicationFactory();
        var jwt = await Register(f.CreateClient(), "dp@example.com");
        var secret = await CreateKey(Authed(f, jwt));

        // Data-plane request authenticates with the raw secret via X-API-Key.
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-API-Key", secret);
        var res = await c.GetAsync("/v1/whoami");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // The resolved tenant must match the one carried by the JWT that minted the key.
        var who = await res.Content.ReadFromJsonAsync<JsonElement>();
        var jwtTenant = DecodeTenant(jwt);
        Assert.Equal(jwtTenant, who.GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task ApiKey_AcceptsBearerScheme()
    {
        await using var f = new TestWebApplicationFactory();
        var secret = await CreateKey(Authed(f, await Register(f.CreateClient(), "bearer@example.com")));

        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/v1/whoami")).StatusCode);
    }

    [Fact]
    public async Task ApiKey_Missing_Returns401()
    {
        await using var f = new TestWebApplicationFactory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/v1/whoami")).StatusCode);
    }

    [Fact]
    public async Task ApiKey_Revoked_Returns401()
    {
        await using var f = new TestWebApplicationFactory();
        var jwtClient = Authed(f, await Register(f.CreateClient(), "revoked-key@example.com"));
        var created = await (await jwtClient.PostAsJsonAsync("/v1/keys", new { label = "z" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var secret = created.GetProperty("secret").GetString()!;
        await jwtClient.DeleteAsync($"/v1/keys/{created.GetProperty("id").GetString()}");

        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-API-Key", secret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/v1/whoami")).StatusCode);
    }

    private static string DecodeTenant(string token)
    {
        var part = token.Split('.')[1];
        part = part.Replace('-', '+').Replace('_', '/');
        switch (part.Length % 4) { case 2: part += "=="; break; case 3: part += "="; break; }
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(part));
        return JsonSerializer.Deserialize<JsonElement>(json).GetProperty("tenant").GetString()!;
    }
}
