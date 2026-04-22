using Xunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using AutoCommerce.StoreManagement.Infrastructure;
using AutoCommerce.StoreManagement.Services;
using AutoCommerce.Shared.Events;

namespace AutoCommerce.StoreManagement.Tests;

public class StoreServiceTests
{
    private readonly Mock<IShopifyClient> _shopifyMock;
    private readonly Mock<IBrainClient> _brainMock;
    private readonly Mock<ILogger<StoreService>> _loggerMock;
    private readonly StoreDbContext _db;
    private readonly StoreService _service;

    public StoreServiceTests()
    {
        _shopifyMock = new Mock<IShopifyClient>();
        _brainMock = new Mock<IBrainClient>();
        _loggerMock = new Mock<ILogger<StoreService>>();

        var opts = new DbContextOptionsBuilder<StoreDbContext>()
            .UseInMemoryDatabase($"StoreServiceTests-{Guid.NewGuid()}")
            .Options;
        _db = new StoreDbContext(opts);

        _brainMock.Setup(x => x.PublishEventAsync(It.IsAny<DomainEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _service = new StoreService(_shopifyMock.Object, _brainMock.Object, _loggerMock.Object, _db);
    }

    [Fact]
    public async Task InitializeStoreAsync_WhenConnectionSucceeds_CreatesCollectionsAndPages()
    {
        _shopifyMock.Setup(x => x.TestConnectionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _shopifyMock.Setup(x => x.CreateCollectionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string t, string? d, CancellationToken _) => new ShopifyCollection(1, t, d));
        _shopifyMock.Setup(x => x.CreatePageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string t, string h, string b, CancellationToken _) => new ShopifyPage(1, t, h, b));

        await _service.InitializeStoreAsync();

        _shopifyMock.Verify(x => x.CreateCollectionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(3));
        _shopifyMock.Verify(x => x.CreatePageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(3));
    }

    [Fact]
    public async Task InitializeStoreAsync_WhenConnectionFails_Throws()
    {
        _shopifyMock.Setup(x => x.TestConnectionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.InitializeStoreAsync());
    }

    [Fact]
    public async Task SyncProductAsync_WhenNewProduct_CreatesPublishesAndPersistsMapping()
    {
        var brainId = Guid.NewGuid();
        _shopifyMock.Setup(x => x.CreateProductAsync(It.IsAny<ShopifyProductInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShopifyProductOutput(1000, "T", 99m, false));
        _shopifyMock.Setup(x => x.PublishProductAsync(It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await _service.SyncProductAsync(brainId, "T", "D", 99m, null);

        _shopifyMock.Verify(x => x.CreateProductAsync(It.IsAny<ShopifyProductInput>(), It.IsAny<CancellationToken>()), Times.Once);
        _shopifyMock.Verify(x => x.PublishProductAsync(1000, It.IsAny<CancellationToken>()), Times.Once);

        var row = await _db.ProductSyncs.FirstOrDefaultAsync(r => r.BrainProductId == brainId);
        row.Should().NotBeNull();
        row!.ShopifyProductId.Should().Be(1000);
    }

    [Fact]
    public async Task SyncProductAsync_WhenExisting_UpdatesInsteadOfCreating()
    {
        var brainId = Guid.NewGuid();
        _db.ProductSyncs.Add(new Domain.ShopifyProductSync
        {
            BrainProductId = brainId, ShopifyProductId = 1234, Title = "Old", Price = 10m
        });
        await _db.SaveChangesAsync();

        _shopifyMock.Setup(x => x.UpdateProductAsync(1234, It.IsAny<ShopifyProductInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShopifyProductOutput(1234, "New", 20m, true));

        await _service.SyncProductAsync(brainId, "New", "Desc", 20m, null);

        _shopifyMock.Verify(x => x.UpdateProductAsync(1234, It.IsAny<ShopifyProductInput>(), It.IsAny<CancellationToken>()), Times.Once);
        _shopifyMock.Verify(x => x.CreateProductAsync(It.IsAny<ShopifyProductInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateProductPriceAsync_WhenMapped_UpdatesPriceAndPublishesEvent()
    {
        var brainId = Guid.NewGuid();
        _db.ProductSyncs.Add(new Domain.ShopifyProductSync
        {
            BrainProductId = brainId, ShopifyProductId = 5000, Title = "T", Price = 10m
        });
        await _db.SaveChangesAsync();

        _shopifyMock.Setup(x => x.GetProductAsync(5000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShopifyProductOutput(5000, "T", 10m, true));
        _shopifyMock.Setup(x => x.UpdateProductAsync(5000, It.IsAny<ShopifyProductInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShopifyProductOutput(5000, "T", 50m, true));

        await _service.UpdateProductPriceAsync(brainId, 50m);

        _shopifyMock.Verify(x => x.UpdateProductAsync(5000,
            It.Is<ShopifyProductInput>(i => i.Price == 50m), It.IsAny<CancellationToken>()), Times.Once);
        _brainMock.Verify(x => x.PublishEventAsync(
            It.Is<DomainEvent>(e => e.Type == EventTypes.PriceUpdated),
            It.IsAny<CancellationToken>()), Times.Once);

        var row = await _db.ProductSyncs.FirstAsync(r => r.BrainProductId == brainId);
        row.Price.Should().Be(50m);
    }

    [Fact]
    public async Task UpdateProductPriceAsync_WhenNotMapped_DoesNothing()
    {
        await _service.UpdateProductPriceAsync(Guid.NewGuid(), 99m);
        _shopifyMock.Verify(x => x.GetProductAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateProductStatusAsync_WhenMapped_CallsShopify()
    {
        var brainId = Guid.NewGuid();
        _db.ProductSyncs.Add(new Domain.ShopifyProductSync
        {
            BrainProductId = brainId, ShopifyProductId = 7777, Title = "T", Price = 1m
        });
        await _db.SaveChangesAsync();

        _shopifyMock.Setup(x => x.SetProductStatusAsync(7777, "archived", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _service.UpdateProductStatusAsync(brainId, "archived");

        _shopifyMock.Verify(x => x.SetProductStatusAsync(7777, "archived", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateProductStockAsync_WhenMapped_UpdatesInventory()
    {
        var brainId = Guid.NewGuid();
        _db.ProductSyncs.Add(new Domain.ShopifyProductSync
        {
            BrainProductId = brainId, ShopifyProductId = 8888, Title = "T", Price = 1m
        });
        await _db.SaveChangesAsync();

        _shopifyMock.Setup(x => x.UpdateInventoryAsync(8888, 25, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _service.UpdateProductStockAsync(brainId, 25);

        _shopifyMock.Verify(x => x.UpdateInventoryAsync(8888, 25, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateThemeAsync_DelegatesToShopifyClient()
    {
        _shopifyMock.Setup(x => x.UpdateThemeAsync(It.IsAny<ShopifyThemeConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShopifyTheme(1, "Custom", "main"));

        var cfg = new ShopifyThemeConfig("Custom", "Welcome", "Sub", "#000", null);
        var result = await _service.UpdateThemeAsync(cfg);

        result.Name.Should().Be("Custom");
        _shopifyMock.Verify(x => x.UpdateThemeAsync(cfg, It.IsAny<CancellationToken>()), Times.Once);
    }
}
