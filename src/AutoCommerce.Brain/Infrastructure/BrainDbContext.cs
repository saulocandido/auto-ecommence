using AutoCommerce.Brain.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AutoCommerce.Brain.Infrastructure;

public class BrainDbContext : DbContext
{
    public BrainDbContext(DbContextOptions<BrainDbContext> options) : base(options) { }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<PricingRule> PricingRules => Set<PricingRule>();
    public DbSet<EventLog> EventLogs => Set<EventLog>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Product>(e =>
        {
            e.HasIndex(p => p.ExternalId).IsUnique();
            e.HasIndex(p => p.Status);
            e.HasIndex(p => p.Category);
            e.Property(p => p.Cost).HasPrecision(18, 4);
            e.Property(p => p.Price).HasPrecision(18, 4);
        });

        b.Entity<Order>(e =>
        {
            e.HasIndex(o => o.ShopOrderId).IsUnique();
            e.HasIndex(o => o.Status);
            e.Property(o => o.Total).HasPrecision(18, 4);
            e.HasMany(o => o.Lines).WithOne(l => l.Order!).HasForeignKey(l => l.OrderId);
        });

        b.Entity<OrderLine>(e =>
        {
            e.Property(l => l.UnitPrice).HasPrecision(18, 4);
        });

        b.Entity<PricingRule>(e =>
        {
            e.HasIndex(p => p.Category).IsUnique();
            e.Property(p => p.MinPrice).HasPrecision(18, 4);
            e.Property(p => p.MaxPrice).HasPrecision(18, 4);
        });

        b.Entity<EventLog>(e =>
        {
            // SQLite can't ORDER BY / WHERE on DateTimeOffset directly — store as ticks (long).
            e.Property(x => x.OccurredAt).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => x.Type);
        });

        b.Entity<ApiKey>(e =>
        {
            e.HasIndex(x => x.KeyHash).IsUnique();
        });
    }
}
