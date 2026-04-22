using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using AutoCommerce.StoreManagement.Domain;

namespace AutoCommerce.StoreManagement.Infrastructure;

public class StoreDbContext : DbContext
{
    public StoreDbContext(DbContextOptions<StoreDbContext> options) : base(options) { }

    public DbSet<ShopifyStore> Stores => Set<ShopifyStore>();
    public DbSet<ShopifyProductSync> ProductSyncs => Set<ShopifyProductSync>();
    public DbSet<ShopifyAdminConfig> AdminConfigs => Set<ShopifyAdminConfig>();
    public DbSet<EventCheckpoint> EventCheckpoints => Set<EventCheckpoint>();
    public DbSet<DeadLetterItem> DeadLetters => Set<DeadLetterItem>();
    public DbSet<ShopifyAutomationConfig> AutomationConfigs => Set<ShopifyAutomationConfig>();
    public DbSet<ShopifyAutomationRun> AutomationRuns => Set<ShopifyAutomationRun>();
    public DbSet<ShopifyAutomationProduct> AutomationProducts => Set<ShopifyAutomationProduct>();
    public DbSet<ShopifyAutomationLog> AutomationLogs => Set<ShopifyAutomationLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite can't ORDER BY a TEXT-serialized DateTimeOffset (the provider refuses to
        // translate it). Encoding every DateTimeOffset as a single long preserves the offset
        // and lets the provider emit native ORDER BY / comparison SQL.
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToBinaryConverter>();
        configurationBuilder.Properties<DateTimeOffset?>()
            .HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<ShopifyStore>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ShopName).IsRequired();
            e.Property(x => x.AccessToken).IsRequired();
            e.Property(x => x.ApiKey).IsRequired();
        });

        b.Entity<ShopifyProductSync>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.BrainProductId).IsUnique();
            e.HasIndex(x => x.ShopifyProductId);
            e.Property(x => x.BrainProductId).IsRequired();
            e.Property(x => x.ShopifyProductId).IsRequired();
            e.Property(x => x.Title).IsRequired();
            e.Property(x => x.SyncStatus).IsRequired();
            e.Property(x => x.PublicationStatus).IsRequired();
            e.Property(x => x.VariantIdsJson).IsRequired();
        });

        b.Entity<ShopifyAdminConfig>(e =>
        {
            e.HasKey(x => x.Id);
        });

        b.Entity<EventCheckpoint>(e =>
        {
            e.HasKey(x => x.EventType);
        });

        b.Entity<DeadLetterItem>(e =>
        {
            e.HasKey(x => x.Id);
        });

        b.Entity<ShopifyAutomationConfig>(e =>
        {
            e.HasKey(x => x.Id);
        });

        b.Entity<ShopifyAutomationRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Status);
        });

        b.Entity<ShopifyAutomationProduct>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.RunId);
            e.HasIndex(x => x.BrainProductId);
        });

        b.Entity<ShopifyAutomationLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.RunId);
        });
    }
}
