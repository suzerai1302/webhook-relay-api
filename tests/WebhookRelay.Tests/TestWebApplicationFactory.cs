using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using WebhookRelay.Core.Abstractions;
using WebhookRelay.Infrastructure;
using WebhookRelay.Tests.Fakes;

namespace WebhookRelay.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    // Controllable clock and recordable HTTP delivery so the dispatcher runs deterministically.
    public FakeClock Clock { get; } = new();
    public FakeHttpDelivery Http { get; } = new();
    public FakeStripeGateway Stripe { get; } = new();

    // Config keys to override (e.g. lower plan limits to exercise quota guards). Set before
    // the first client is created.
    public Dictionary<string, string?> ConfigOverrides { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        _connection.Open();
        builder.ConfigureAppConfiguration(cfg => cfg.AddInMemoryCollection(ConfigOverrides));
        builder.ConfigureServices(services =>
        {
            services.AddDbContext<WebhookRelayDbContext>(options => options.UseSqlite(_connection));
            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<IHttpDelivery>(Http);
            services.AddSingleton<IStripeGateway>(Stripe);
            services.AddScoped<DeliveryProcessor>();

            using var scope = services.BuildServiceProvider().CreateScope();
            scope.ServiceProvider.GetRequiredService<WebhookRelayDbContext>().Database.EnsureCreated();
        });
    }

    // Runs one dispatch pass through the real DeliveryProcessor, exactly as the background
    // loop would — drives delivery tests without timing races.
    public async Task DispatchOnceAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DeliveryProcessor>()
            .ProcessDueAsync(CancellationToken.None);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}
