using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using WebhookRelay.API;
using WebhookRelay.Core.Abstractions;
using WebhookRelay.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Honor the platform-assigned port (Render sets PORT); ignored locally and under tests.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var isTesting = builder.Environment.IsEnvironment("Testing");

builder.Services.AddOpenApi();

// Tenant resolution context (set by auth) + clock. Registered in all environments.
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddSingleton<IClock, SystemClock>();

// In the Testing environment the test host supplies the DbContext (SQLite) and drives
// the dispatcher manually — so we skip Postgres, the real HTTP delivery client, and the
// background loop here.
if (!isTesting)
{
    var pgConn = builder.Configuration.GetConnectionString("Postgres");
    var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        // Render/Neon expose a postgres:// URL; convert it to an Npgsql connection string.
        var uri = new Uri(databaseUrl);
        var creds = uri.UserInfo.Split(':', 2);
        var dbPort = uri.Port == -1 ? 5432 : uri.Port;
        var user = Uri.UnescapeDataString(creds[0]);
        var pass = creds.Length > 1 ? Uri.UnescapeDataString(creds[1]) : "";
        pgConn = $"Host={uri.Host};Port={dbPort};Database={uri.AbsolutePath.TrimStart('/')};" +
                 $"Username={user};Password={pass};SSL Mode=Require;Trust Server Certificate=true";
    }

    builder.Services.AddDbContext<WebhookRelayDbContext>(options => options.UseNpgsql(pgConn));
}

var app = builder.Build();

// Render terminates TLS at a proxy and forwards plain HTTP with X-Forwarded-Proto.
// Honor it so Request.Scheme is "https" — otherwise the OpenAPI server URL is http://
// and Scalar's browser calls get blocked as mixed content. KnownProxies/Networks are
// cleared because the proxy isn't loopback.
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
forwardedOptions.KnownIPNetworks.Clear();
forwardedOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedOptions);

// Apply pending migrations on startup (skipped under tests, which use SQLite).
if (!isTesting)
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<WebhookRelayDbContext>().Database.Migrate();
}

// OpenAPI spec + Scalar interactive docs, live in all environments for the demo.
app.MapOpenApi();
app.MapScalarApiReference();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Exposed so the test project's WebApplicationFactory<Program> can boot the real app.
public partial class Program { }
