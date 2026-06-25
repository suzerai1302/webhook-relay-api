using Microsoft.EntityFrameworkCore;
using WebhookRelay.Core.Abstractions;
using WebhookRelay.Core.Entities;

namespace WebhookRelay.Infrastructure;

public class WebhookRelayDbContext : DbContext
{
    private readonly ITenantContext _tenant;

    public WebhookRelayDbContext(DbContextOptions<WebhookRelayDbContext> options, ITenantContext tenant)
        : base(options)
    {
        _tenant = tenant;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Endpoint> Endpoints => Set<Endpoint>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<StripeEvent> StripeEvents => Set<StripeEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Global idempotency ledger keyed by Stripe's event id; not tenant-scoped.
        b.Entity<StripeEvent>(e => e.HasKey(x => x.Id));

        b.Entity<Tenant>(e => e.Property(t => t.StripeCustomerId).HasMaxLength(255));

        b.Entity<User>(e =>
        {
            e.HasIndex(u => u.Email).IsUnique();
            e.HasIndex(u => u.TenantId);
            e.HasQueryFilter(u => u.TenantId == _tenant.TenantId);
        });

        b.Entity<ApiKey>(e =>
        {
            e.HasIndex(k => k.Hash).IsUnique();
            e.HasIndex(k => k.TenantId);
            e.HasQueryFilter(k => k.TenantId == _tenant.TenantId);
        });

        b.Entity<Endpoint>(e =>
        {
            e.HasIndex(x => x.TenantId);
            e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
        });

        b.Entity<Event>(e =>
        {
            e.HasIndex(x => x.TenantId);
            // Idempotency is scoped per tenant; nulls allowed (most events have no key).
            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
            e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
        });

        b.Entity<Delivery>(e =>
        {
            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
        });
    }
}
