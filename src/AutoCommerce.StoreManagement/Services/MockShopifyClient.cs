using System.Collections.Concurrent;

namespace AutoCommerce.StoreManagement.Services;

public class MockShopifyClient : IShopifyClient
{
    private readonly ILogger<MockShopifyClient> _logger;
    private readonly ConcurrentDictionary<long, ShopifyProductOutput> _products = new();
    private readonly ConcurrentDictionary<long, List<string>> _tags = new();
    private readonly ConcurrentDictionary<long, List<ShopifyMetafield>> _metafields = new();
    private readonly ConcurrentDictionary<long, List<string>> _productImages = new();
    private readonly ConcurrentDictionary<long, HashSet<long>> _collectionMembership = new();
    private readonly ConcurrentDictionary<long, List<string>> _productChannels = new();
    private readonly ConcurrentDictionary<long, ShopifyCollection> _collections = new();
    private readonly ConcurrentDictionary<long, ShopifyPage> _pages = new();
    private ShopifyThemeConfig _theme = new("Default", "Welcome", "Shop the latest products", "#2563eb", null);
    private long _nextProductId = 1000;
    private long _nextCollectionId = 2000;
    private long _nextPageId = 3000;
    private long _nextVariantId = 4000;

    public MockShopifyClient(ILogger<MockShopifyClient> logger) { _logger = logger; }

    public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<ShopifyProductOutput> CreateProductAsync(ShopifyProductInput input, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextProductId);
        var variantIds = (input.Variants ?? Array.Empty<ShopifyVariant>())
            .Select(_ => Interlocked.Increment(ref _nextVariantId)).ToList();
        var output = new ShopifyProductOutput(id, input.Title, input.Price, false,
            input.Status, input.StockQuantity, variantIds, input.Tags?.ToList() ?? new List<string>(),
            DateTimeOffset.UtcNow);
        _products[id] = output;
        _tags[id] = (input.Tags ?? Array.Empty<string>()).ToList();
        _metafields[id] = new List<ShopifyMetafield>();
        _productImages[id] = new List<string>();
        if (!string.IsNullOrWhiteSpace(input.ImageUrl)) _productImages[id].Add(input.ImageUrl);
        if (input.ImageUrls != null) _productImages[id].AddRange(input.ImageUrls);
        _logger.LogInformation("Mock created product {Id} '{Title}'", id, input.Title);
        return Task.FromResult(output);
    }

    public Task<ShopifyProductOutput> UpdateProductAsync(long productId, ShopifyProductInput input, CancellationToken ct = default)
    {
        if (!_products.TryGetValue(productId, out var existing))
            throw new InvalidOperationException($"Product {productId} not found");
        var updated = existing with
        {
            Title = input.Title,
            Price = input.Price,
            Status = input.Status,
            StockQuantity = input.StockQuantity,
            Tags = input.Tags?.ToList() ?? existing.Tags,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _products[productId] = updated;
        _logger.LogInformation("Mock updated product {Id}", productId);
        return Task.FromResult(updated);
    }

    public Task<ShopifyProductOutput?> GetProductAsync(long productId, CancellationToken ct = default)
    {
        _products.TryGetValue(productId, out var p);
        return Task.FromResult(p);
    }

    public Task<bool> PublishProductAsync(long productId, CancellationToken ct = default)
    {
        if (!_products.TryGetValue(productId, out var p)) return Task.FromResult(false);
        _products[productId] = p with { Published = true, UpdatedAt = DateTimeOffset.UtcNow };
        return Task.FromResult(true);
    }

    public Task<bool> DeleteProductAsync(long productId, CancellationToken ct = default) =>
        Task.FromResult(_products.TryRemove(productId, out _));

    public Task<bool> SetProductStatusAsync(long productId, string status, CancellationToken ct = default)
    {
        if (!_products.TryGetValue(productId, out var p)) return Task.FromResult(false);
        _products[productId] = p with { Status = status, UpdatedAt = DateTimeOffset.UtcNow };
        return Task.FromResult(true);
    }

    public Task<bool> UpdateInventoryAsync(long productId, int quantity, CancellationToken ct = default)
    {
        if (!_products.TryGetValue(productId, out var p)) return Task.FromResult(false);
        _products[productId] = p with { StockQuantity = quantity, UpdatedAt = DateTimeOffset.UtcNow };
        return Task.FromResult(true);
    }

    public Task AddTagsAsync(long productId, IReadOnlyList<string> tags, CancellationToken ct = default)
    {
        var list = _tags.GetOrAdd(productId, _ => new List<string>());
        lock (list)
            foreach (var t in tags) if (!list.Contains(t)) list.Add(t);
        if (_products.TryGetValue(productId, out var p))
            _products[productId] = p with { Tags = list.ToList() };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetTagsAsync(long productId, CancellationToken ct = default)
    {
        var list = _tags.TryGetValue(productId, out var v) ? v : new List<string>();
        return Task.FromResult<IReadOnlyList<string>>(list.ToList());
    }

    public Task SetMetafieldAsync(long productId, ShopifyMetafield metafield, CancellationToken ct = default)
    {
        var list = _metafields.GetOrAdd(productId, _ => new List<ShopifyMetafield>());
        lock (list)
        {
            var idx = list.FindIndex(m => m.Namespace == metafield.Namespace && m.Key == metafield.Key);
            if (idx >= 0) list[idx] = metafield; else list.Add(metafield);
        }
        return Task.CompletedTask;
    }

    public Task<ShopifyMetafield?> GetMetafieldAsync(long productId, string ns, string key, CancellationToken ct = default)
    {
        if (!_metafields.TryGetValue(productId, out var list)) return Task.FromResult<ShopifyMetafield?>(null);
        lock (list) return Task.FromResult(list.FirstOrDefault(m => m.Namespace == ns && m.Key == key));
    }

    public Task<long?> FindProductByMetafieldAsync(string ns, string key, string value, CancellationToken ct = default)
    {
        foreach (var kv in _metafields)
        {
            lock (kv.Value)
            {
                if (kv.Value.Any(m => m.Namespace == ns && m.Key == key && m.Value == value))
                    return Task.FromResult<long?>(kv.Key);
            }
        }
        return Task.FromResult<long?>(null);
    }

    public Task UploadImagesAsync(long productId, IReadOnlyList<string> imageUrls, string? altText, CancellationToken ct = default)
    {
        var list = _productImages.GetOrAdd(productId, _ => new List<string>());
        lock (list) foreach (var u in imageUrls) if (!list.Contains(u)) list.Add(u);
        return Task.CompletedTask;
    }

    public Task<ShopifyCollection> CreateCollectionAsync(string title, string? description, CancellationToken ct = default)
    {
        var existing = _collections.Values.FirstOrDefault(c => c.Title == title);
        if (existing != null) return Task.FromResult(existing);
        var id = Interlocked.Increment(ref _nextCollectionId);
        var col = new ShopifyCollection(id, title, description);
        _collections[id] = col;
        return Task.FromResult(col);
    }

    public Task<IReadOnlyList<ShopifyCollection>> ListCollectionsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ShopifyCollection>>(_collections.Values.ToList());

    public Task AssignToCollectionAsync(long productId, long collectionId, CancellationToken ct = default)
    {
        var set = _collectionMembership.GetOrAdd(collectionId, _ => new HashSet<long>());
        lock (set) set.Add(productId);
        return Task.CompletedTask;
    }

    public Task<ShopifyPage> CreatePageAsync(string title, string handle, string bodyHtml, CancellationToken ct = default)
    {
        var existing = _pages.Values.FirstOrDefault(p => p.Handle == handle);
        if (existing != null) return Task.FromResult(existing);
        var id = Interlocked.Increment(ref _nextPageId);
        var page = new ShopifyPage(id, title, handle, bodyHtml);
        _pages[id] = page;
        return Task.FromResult(page);
    }

    public Task<IReadOnlyList<ShopifyPage>> ListPagesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ShopifyPage>>(_pages.Values.ToList());

    public Task<ShopifyTheme> UpdateThemeAsync(ShopifyThemeConfig config, CancellationToken ct = default)
    {
        _theme = config;
        return Task.FromResult(new ShopifyTheme(1, config.ThemeName ?? "Default", "main"));
    }

    public Task<ShopifyThemeConfig> GetThemeConfigAsync(CancellationToken ct = default)
        => Task.FromResult(_theme);

    public Task PublishToChannelsAsync(long productId, IReadOnlyList<string> channels, CancellationToken ct = default)
    {
        _productChannels[productId] = channels.ToList();
        return Task.CompletedTask;
    }
}
