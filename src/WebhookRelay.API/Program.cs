using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using WebhookRelay.API;
using WebhookRelay.API.Endpoints;
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
builder.Services.AddSingleton<IClock, WebhookRelay.Infrastructure.SystemClock>();
builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
builder.Services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
builder.Services.AddSingleton<IApiKeyHasher, Sha256ApiKeyHasher>();

// Delivery pipeline pass. Scoped (owns a DbContext per dispatch); driven by the background
// Dispatcher in real runs and explicitly by the test factory under Testing.
builder.Services.AddScoped<DeliveryProcessor>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!)),
        };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>(ApiKeyAuthHandler.SchemeName, null);
builder.Services.AddAuthorization();

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

    // Real outbound HTTP + the background loop that drains due deliveries.
    builder.Services.AddHttpClient<IHttpDelivery, HttpDelivery>();
    builder.Services.AddHostedService(sp => new Dispatcher(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ILogger<Dispatcher>>(),
        TimeSpan.FromSeconds(10)));
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

app.UseAuthentication();

// Resolve the tenant from the JWT `tenant` claim so the DbContext query filter is active
// on every JWT-authenticated request. (API-key auth sets the tenant in its own handler.)
app.Use(async (ctx, next) =>
{
    var claim = ctx.User.FindFirst("tenant")?.Value;
    if (Guid.TryParse(claim, out var tenantId))
        ctx.RequestServices.GetRequiredService<ITenantContext>().TenantId = tenantId;
    await next();
});

app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapAuthEndpoints();
app.MapKeysEndpoints();
app.MapEndpointsEndpoints();
app.MapEventsEndpoints();
app.MapDeliveriesEndpoints();

// Data-plane key check: confirms an API key is valid and reports the tenant it resolves to.
app.MapGet("/v1/whoami", (ITenantContext tenant) => Results.Ok(new { tenantId = tenant.TenantId }))
    .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
    {
        AuthenticationSchemes = ApiKeyAuthHandler.SchemeName
    });

app.Run();

// Exposed so the test project's WebApplicationFactory<Program> can boot the real app.
public partial class Program { }
