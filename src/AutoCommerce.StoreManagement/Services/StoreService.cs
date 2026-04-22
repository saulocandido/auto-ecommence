using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AutoCommerce.Shared.Events;
using AutoCommerce.StoreManagement.Domain;
using AutoCommerce.StoreManagement.Infrastructure;

namespace AutoCommerce.StoreManagement.Services;

public interface IStoreService
{
    Task InitializeStoreAsync(CancellationToken ct = default);
    Task SyncProductAsync(Guid brainProductId, string title, string description, decimal price, string? imageUrl,
        IReadOnlyList<ShopifyVariant>? variants = null, int stockQuantity = 0, CancellationToken ct = default);
    Task UpdateProductPriceAsync(Guid brainProductId, decimal newPrice, CancellationToken ct = default);
    Task UpdateProductStatusAsync(Guid brainProductId, string status, CancellationToken ct = default);
    Task UpdateProductStockAsync(Guid brainProductId, int quantity, CancellationToken ct = default);
    Task<ShopifyTheme> UpdateThemeAsync(ShopifyThemeConfig config, CancellationToken ct = default);
    Task<ShopifyThemeConfig> GetThemeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ShopifyPage>> ListPagesAsync(CancellationToken ct = default);
    Task<ShopifyPage> UpsertPageAsync(string title, string handle, string bodyHtml, CancellationToken ct = default);
}

public class StoreService : IStoreService
{
    private static readonly (string Title, string? Description)[] DefaultCollections =
    {
        ("New Arrivals", "Recently added products"),
        ("Best Sellers", "Top-performing products"),
        ("Sale", "Discounted items")
    };

    private static readonly (string Title, string Handle, string Body)[] DefaultLegalPages =
    {
        ("Privacy Policy", "privacy-policy", "<h1>Privacy Policy</h1><p>Placeholder — customise in dashboard.</p>"),
        ("Terms of Service", "terms-of-service", "<h1>Terms of Service</h1><p>Placeholder — customise in dashboard.</p>"),
        ("Refund Policy", "refund-policy", "<h1>Refund Policy</h1><p>Placeholder — customise in dashboard.</p>"),
        ("Shipping Policy", "shipping-policy", "<h1>Shipping Policy</h1><p>Placeholder — customise in dashboard.</p>")
    };

    private readonly IShopifyClient _shopify;
    private readonly IBrainClient _brain;
    private readonly StoreDbContext? _db;
    private readonly ILogger<StoreService> _logger;
    private readonly Dictionary<Guid, long> _memoryMap = new();

    public StoreService(IShopifyClient shopify, IBrainClient brain, ILogger<StoreService> logger, StoreDbContext? db = null)
    {
        _shopify = shopify;
        _brain = brain;
        _logger = logger;
        _db = db;
    }

    public async Task InitializeStoreAsync(CancellationToken ct = default)
    {
        var connected = await _shopify.TestConnectionAsync(ct);
        if (!connected) throw new InvalidOperationException("Failed to connect to Shopify");

        foreach (var (title, description) in DefaultCollections)
            await _shopify.CreateCollectionAsync(title, description, ct);

        foreach (var (title, handle, body) in DefaultLegalPages)
            await _shopify.CreatePageAsync(title, handle, body, ct);

        _logger.LogInformation("Store initialised: collections and legal pages ensured");
    }

    public async Task SyncProductAsync(Guid brainProductId, string title, string description, decimal price, string? imageUrl,
        IReadOnlyList<ShopifyVariant>? variants = null, int stockQuantity = 0, CancellationToken ct = default)
    {
        var existingShopifyId = await GetShopifyIdAsync(brainProductId, ct);

        var input = new ShopifyProductInput(
            title, description, price, imageUrl,
            new Dictionary<string, string> { { "brain_product_id", brainProductId.ToString() } },
            variants, stockQuantity, "active");

        ShopifyProductOutput output;
        if (existingShopifyId.HasValue)
        {
            output = await _shopify.UpdateProductAsync(existingShopifyId.Value, input, ct);
            _logger.LogInformation("Updated product {BrainId} → Shopify {ShopifyId}", brainProductId, existingShopifyId.Value);
        }
        else
        {
            output = await _shopify.CreateProductAsync(input, ct);
            await _shopify.PublishProductAsync(output.Id, ct);
            await PersistMappingAsync(brainProductId, output.Id, title, price, ct);
            _logger.LogInformation("Created product {BrainId} → Shopify {ShopifyId}", brainProductId, output.Id);
        }

        await _brain.PublishEventAsync(
            DomainEvent.Create("store.product_synced", "store-manager",
                new { brainProductId, shopifyId = output.Id }), ct);
    }

    public async Task UpdateProductPriceAsync(Guid brainProductId, decimal newPrice, CancellationToken ct = default)
    {
        var shopifyId = await GetShopifyIdAsync(brainProductId, ct);
        if (!shopifyId.HasValue)
        {
            _logger.LogWarning("Product {BrainId} not mapped to Shopify", brainProductId);
            return;
        }

        var existing = await _shopify.GetProductAsync(shopifyId.Value, ct);
        if (existing == null) return;

        var input = new ShopifyProductInput(existing.Title, "", newPrice, null, null);
        await _shopify.UpdateProductAsync(shopifyId.Value, input, ct);

        if (_db != null)
        {
            var row = await _db.ProductSyncs.FirstOrDefaultAsync(x => x.BrainProductId == brainProductId, ct);
            if (row != null) { row.Price = newPrice; row.SyncedAt = DateTimeOffset.UtcNow; await _db.SaveChangesAsync(ct); }
        }

        await _brain.PublishEventAsync(
            DomainEvent.Create(EventTypes.PriceUpdated, "store-manager",
                new { brainProductId, shopifyId = shopifyId.Value, newPrice }), ct);

        _logger.LogInformation("Updated price for {BrainId}: {Price}", brainProductId, newPrice);
    }

    public async Task UpdateProductStatusAsync(Guid brainProductId, string status, CancellationToken ct = default)
    {
        var shopifyId = await GetShopifyIdAsync(brainProductId, ct);
        if (!shopifyId.HasValue)
        {
            _logger.LogWarning("Product {BrainId} not mapped to Shopify", brainProductId);
            return;
        }

        await _shopify.SetProductStatusAsync(shopifyId.Value, status, ct);
        _logger.LogInformation("Set status for {BrainId}: {Status}", brainProductId, status);
    }

    public async Task UpdateProductStockAsync(Guid brainProductId, int quantity, CancellationToken ct = default)
    {
        var shopifyId = await GetShopifyIdAsync(brainProductId, ct);
        if (!shopifyId.HasValue)
        {
            _logger.LogWarning("Product {BrainId} not mapped to Shopify", brainProductId);
            return;
        }

        await _shopify.UpdateInventoryAsync(shopifyId.Value, quantity, ct);
        _logger.LogInformation("Updated stock for {BrainId}: {Qty}", brainProductId, quantity);
    }

    public Task<ShopifyTheme> UpdateThemeAsync(ShopifyThemeConfig config, CancellationToken ct = default)
        => _shopify.UpdateThemeAsync(config, ct);

    public Task<ShopifyThemeConfig> GetThemeAsync(CancellationToken ct = default)
        => _shopify.GetThemeConfigAsync(ct);

    public Task<IReadOnlyList<ShopifyPage>> ListPagesAsync(CancellationToken ct = default)
        => _shopify.ListPagesAsync(ct);

    public Task<ShopifyPage> UpsertPageAsync(string title, string handle, string bodyHtml, CancellationToken ct = default)
        => _shopify.CreatePageAsync(title, handle, bodyHtml, ct);

    private async Task<long?> GetShopifyIdAsync(Guid brainProductId, CancellationToken ct)
    {
        if (_db != null)
        {
            var row = await _db.ProductSyncs.AsNoTracking()
                .FirstOrDefaultAsync(x => x.BrainProductId == brainProductId, ct);
            if (row != null) return row.ShopifyProductId;
        }
        return _memoryMap.TryGetValue(brainProductId, out var id) ? id : null;
    }

    private async Task PersistMappingAsync(Guid brainProductId, long shopifyProductId, string title, decimal price, CancellationToken ct)
    {
        _memoryMap[brainProductId] = shopifyProductId;
        if (_db == null) return;

        var existing = await _db.ProductSyncs.FirstOrDefaultAsync(x => x.BrainProductId == brainProductId, ct);
        if (existing == null)
        {
            _db.ProductSyncs.Add(new ShopifyProductSync
            {
                BrainProductId = brainProductId,
                ShopifyProductId = shopifyProductId,
                Title = title,
                Price = price,
                SyncedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.ShopifyProductId = shopifyProductId;
            existing.Title = title;
            existing.Price = price;
            existing.SyncedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }
}
