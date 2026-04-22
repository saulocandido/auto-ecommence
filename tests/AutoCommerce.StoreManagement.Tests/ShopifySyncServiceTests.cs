using Xunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;
using AutoCommerce.StoreManagement.Domain;
using AutoCommerce.StoreManagement.Infrastructure;
using AutoCommerce.StoreManagement.Services;

namespace AutoCommerce.StoreManagement.Tests;

public class ShopifySyncServiceTests : IAsyncLifetime
{
    private StoreDbContext _db = null!;
    private MockShopifyClient _shopify = null!;
    private StubBrainClient _brain = null!;
    private ShopifyMetrics _metrics = null!;
    private ShopifySyncService _sut = null!;

    public Task InitializeAsync()
    {
        var opts = new DbContextOptionsBuilder<StoreDbContext>()
            .UseInMemoryDatabase($"SyncTests-{Guid.NewGuid()}").Options;
        _db = new StoreDbContext(opts);
        _db.Database.EnsureCreated();

        _shopify = new MockShopifyClient(NullLogger<MockShopifyClient>.Instance);
        _brain = new StubBrainClient();
        _metrics = new ShopifyMetrics();
        var retry = new ExponentialBackoffRetryPolicy(NullLogger<ExponentialBackoffRetryPolicy>.Instance, 2, 10);

        _sut = new ShopifySyncService(_shopify, _brain, _db, retry, _metrics, NullLogger<ShopifySyncService>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    private ProductResponse MakeProduct(Guid id, decimal price = 19.99m, int stock = 10, string status = "Active")
    {
        var supplier = new SupplierListing("s1", "ext-1", 5m, "USD", 5, 4.5, stock, "http://supplier");
        return new ProductResponse(
            id, "ext-1", "Widget", "General", "A widget",
            new[] { "http://img/1.jpg" }, new[] { "gadget" },
            "US", 0.9, 5m, price, 50, status, "s1",
            new[] { supplier }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task SyncProductAsync_NewProduct_CreatesAndPersistsRowAndPublishesEvent()
    {
        var id = Guid.NewGuid();
        _brain.Products[id] = MakeProduct(id);

        var result = await _sut.SyncProductAsync(id);

        result.Error.Should().BeNull();
        result.ShopifyProductId.Should().NotBeNull();
        var row = await _db.ProductSyncs.FirstAsync(x => x.BrainProductId == id);
        row.ShopifyProductId.Should().Be(result.ShopifyProductId!.Value);
        row.SyncStatus.Should().Be(ShopifySyncStatus.Synced.ToString());
        row.ManagedBySystem.Should().BeTrue();
        _brain.Published.Should().Contain(e => e.Type == ShopifyEventTypes.ProductCreated);
        _metrics.Get(ShopifyMetrics.Names.ProductsCreated).Should().Be(1);
    }

    [Fact]
    public async Task SyncProductAsync_SecondCall_UpdatesDoesNotCreateDuplicate()
    {
        var id = Guid.NewGuid();
        _brain.Products[id] = MakeProduct(id);

        var first = await _sut.SyncProductAsync(id);
        var second = await _sut.SyncProductAsync(id);

        second.ShopifyProductId.Should().Be(first.ShopifyProductId);
        _metrics.Get(ShopifyMetrics.Names.ProductsCreated).Should().Be(1);
        _metrics.Get(ShopifyMetrics.Names.ProductsUpdated).Should().Be(1);
        _brain.Published.Count(e => e.Type == ShopifyEventTypes.ProductUpdated).Should().Be(1);
    }

    [Fact]
    public async Task SyncProductAsync_ZeroStock_ArchivesWhenAutoArchiveEnabled()
    {
        var id = Guid.NewGuid();
        _brain.Products[id] = MakeProduct(id, stock: 0);

        var result = await _sut.SyncProductAsync(id);

        result.Status.Should().Be("archived");
        _metrics.Get(ShopifyMetrics.Names.ProductsArchived).Should().Be(1);
    }

    [Fact]
    public async Task SyncProductAsync_UnmanagedRow_Skipped()
    {
        var id = Guid.NewGuid();
        _db.ProductSyncs.Add(new ShopifyProductSync
        {
            BrainProductId = id, ShopifyProductId = 42, Title = "external", Price = 1m,
            ManagedBySystem = false
        });
        await _db.SaveChangesAsync();
        _brain.Products[id] = MakeProduct(id);

        var result = await _sut.SyncProductAsync(id);

        result.Status.Should().Be("skipped_unmanaged");
        _metrics.Get(ShopifyMetrics.Names.ProductsCreated).Should().Be(0);
        _metrics.Get(ShopifyMetrics.Names.ProductsUpdated).Should().Be(0);
    }

    [Fact]
    public async Task SyncPriceAsync_UpdatesRowAndEmitsEvent()
    {
        var id = Guid.NewGuid();
        _brain.Products[id] = MakeProduct(id, price: 10m);
        await _sut.SyncProductAsync(id);

        var result = await _sut.SyncPriceAsync(id, 99m);
        result.Error.Should().BeNull();
        var row = await _db.ProductSyncs.FirstAsync(x => x.BrainProductId == id);
        row.Price.Should().Be(99m);
        _brain.Published.Should().Contain(e => e.Type == ShopifyEventTypes.PriceUpdated);
    }

    [Fact]
    public async Task SyncStockAsync_ZeroStock_AutoArchives()
    {
        var id = Guid.NewGuid();
        _brain.Products[id] = MakeProduct(id, stock: 10);
        await _sut.SyncProductAsync(id);

        var result = await _sut.SyncStockAsync(id, 0);

        result.Error.Should().BeNull();
        var row = await _db.ProductSyncs.FirstAsync(x => x.BrainProductId == id);
        row.PublicationStatus.Should().Be("archived");
        _brain.Published.Should().Contain(e => e.Type == ShopifyEventTypes.StockUpdated);
    }

    [Fact]
    public async Task ArchiveProductAsync_EmitsArchivedEvent()
    {
        var id = Guid.NewGuid();
        _brain.Products[id] = MakeProduct(id);
        await _sut.SyncProductAsync(id);

        var result = await _sut.ArchiveProductAsync(id, "manual");

        result.Error.Should().BeNull();
        _brain.Published.Should().Contain(e => e.Type == ShopifyEventTypes.ProductArchived);
    }

    [Fact]
    public async Task SyncProductAsync_BrainMissing_EmitsFailureEventAndDlq()
    {
        var id = Guid.NewGuid();
        var result = await _sut.SyncProductAsync(id);

        result.Error.Should().NotBeNull();
        result.Status.Should().Be("failed");
        _brain.Published.Should().Contain(e => e.Type == ShopifyEventTypes.SyncFailed);
        (await _db.DeadLetters.CountAsync()).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task HealthCheckAsync_ReturnsMetricsAndCounts()
    {
        var id = Guid.NewGuid();
        _brain.Products[id] = MakeProduct(id);
        await _sut.SyncProductAsync(id);

        var h = await _sut.HealthCheckAsync();
        h.Connected.Should().BeTrue();
        h.ManagedProductCount.Should().Be(1);
        h.Metrics.Should().ContainKey(ShopifyMetrics.Names.ProductsCreated);
    }

    [Fact]
    public async Task HandleRemoteProductChangeAsync_RecentSync_DoesNotRe_trigger()
    {
        var id = Guid.NewGuid();
        _brain.Products[id] = MakeProduct(id);
        var first = await _sut.SyncProductAsync(id);
        var published = _brain.Published.Count;

        // Remote updated_at equal to now - should be treated as echo
        await _sut.HandleRemoteProductChangeAsync(first.ShopifyProductId!.Value, DateTimeOffset.UtcNow);

        _brain.Published.Count.Should().Be(published);
    }
}
