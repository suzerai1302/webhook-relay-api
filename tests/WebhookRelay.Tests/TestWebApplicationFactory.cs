using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        _connection.Open();
        builder.ConfigureServices(services =>
        {
            services.AddDbContext<WebhookRelayDbContext>(options => options.UseSqlite(_connection));
            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<IHttpDelivery>(Http);
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
