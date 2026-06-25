using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace WebhookRelay.Tests;

public class OpenApiTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    public OpenApiTests(TestWebApplicationFactory factory) => _factory = factory;

    // Exercises the document/operation transformers (they only run at doc-generation time) and
    // confirms both auth schemes are advertised so Scalar renders the Authorize boxes.
    [Fact]
    public async Task OpenApiDoc_AdvertisesBothSecuritySchemes()
    {
        var res = await _factory.CreateClient().GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        var schemes = doc.GetProperty("components").GetProperty("securitySchemes");
        Assert.True(schemes.TryGetProperty("Bearer", out _));
        Assert.True(schemes.TryGetProperty("ApiKey", out _));
    }
}
