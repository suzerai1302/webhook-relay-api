using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace WebhookRelay.Tests;

public class AuthTests
{
    private static async Task<string> Register(HttpClient c, string email, string pwd)
    {
        var res = await c.PostAsJsonAsync("/auth/register", new { email, password = pwd });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("token").GetString()!;
    }

    private static JsonElement DecodeJwtPayload(string token)
    {
        var part = token.Split('.')[1];
        part = part.Replace('-', '+').Replace('_', '/');
        switch (part.Length % 4) { case 2: part += "=="; break; case 3: part += "="; break; }
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(part));
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    [Fact]
    public async Task Register_Returns201_WithTokenCarryingTenantClaim()
    {
        await using var f = new TestWebApplicationFactory();
        var token = await Register(f.CreateClient(), "a@example.com", "hunter2!");
        var payload = DecodeJwtPayload(token);
        Assert.True(payload.TryGetProperty("tenant", out var tenant));
        Assert.True(Guid.TryParse(tenant.GetString(), out _));
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns409()
    {
        await using var f = new TestWebApplicationFactory();
        var c = f.CreateClient();
        await Register(c, "dup@example.com", "hunter2!");
        var res = await c.PostAsJsonAsync("/auth/register", new { email = "dup@example.com", password = "x" });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        await using var f = new TestWebApplicationFactory();
        var c = f.CreateClient();
        await Register(c, "login@example.com", "right-pass!");
        var res = await c.PostAsJsonAsync("/auth/login", new { email = "login@example.com", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Login_Correct_Returns200_WithToken()
    {
        await using var f = new TestWebApplicationFactory();
        var c = f.CreateClient();
        await Register(c, "ok@example.com", "right-pass!");
        var res = await c.PostAsJsonAsync("/auth/login", new { email = "ok@example.com", password = "right-pass!" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(doc.GetProperty("token").GetString()));
    }
}
