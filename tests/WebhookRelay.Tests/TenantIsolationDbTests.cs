using Microsoft.Extensions.DependencyInjection;
using WebhookRelay.Core.Abstractions;
using WebhookRelay.Core.Entities;
using WebhookRelay.Infrastructure;

namespace WebhookRelay.Tests;

public class TenantIsolationDbTests
{
    [Fact]
    public async Task QueryFilter_HidesOtherTenantsRows()
    {
        await using var factory = new TestWebApplicationFactory();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WebhookRelayDbContext>();
            db.Endpoints.Add(new Endpoint { TenantId = a, Url = "https://a", SigningSecret = "s" });
            db.Endpoints.Add(new Endpoint { TenantId = b, Url = "https://b", SigningSecret = "s" });
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().TenantId = a;
            var db = scope.ServiceProvider.GetRequiredService<WebhookRelayDbContext>();
            var urls = db.Endpoints.Select(e => e.Url).ToList();
            Assert.Equal(new[] { "https://a" }, urls);
        }
    }
}
