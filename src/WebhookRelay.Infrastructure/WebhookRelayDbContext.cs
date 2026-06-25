using Microsoft.EntityFrameworkCore;

namespace WebhookRelay.Infrastructure;

public class WebhookRelayDbContext : DbContext
{
    public WebhookRelayDbContext(DbContextOptions<WebhookRelayDbContext> options) : base(options) { }
}
