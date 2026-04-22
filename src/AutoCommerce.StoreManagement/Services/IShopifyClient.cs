namespace AutoCommerce.StoreManagement.Services;

public record ShopifyVariant(
    string Title,
    decimal Price,
    int StockQuantity,
    string? Sku = null,
    decimal? CompareAtPrice = null
);

public record ShopifyProductInput(
    string Title,
    string Description,
    decimal Price,
    string? ImageUrl,
    Dictionary<string, string>? Metadata,
    IReadOnlyList<ShopifyVariant>? Variants = null,
    int StockQuantity = 0,
    string Status = "active",
    string? Handle = null,
    string? Vendor = null,
    string? ProductType = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? ImageUrls = null,
    string? SeoTitle = null,
    string? SeoDescription = null,
    string? ImageAltText = null,
    decimal? CompareAtPrice = null
);

public record ShopifyProductOutput(
    long Id,
    string Title,
    decimal Price,
    bool Published,
    string Status = "active",
    int StockQuantity = 0,
    IReadOnlyList<long>? VariantIds = null,
    IReadOnlyList<string>? Tags = null,
    DateTimeOffset UpdatedAt = default
);

public record ShopifyCollection(long Id, string Title, string? Description);
public record ShopifyPage(long Id, string Title, string Handle, string BodyHtml);
public record ShopifyTheme(long Id, string Name, string Role);

public record ShopifyThemeConfig(
    string? ThemeName,
    string? HomepageHeading,
    string? HomepageSubheading,
    string? PrimaryColor,
    string? LogoUrl
);

public record ShopifyMetafield(string Namespace, string Key, string Value, string Type = "single_line_text_field");

public interface IShopifyClient
{
    Task<bool> TestConnectionAsync(CancellationToken ct = default);

    // Products
    Task<ShopifyProductOutput> CreateProductAsync(ShopifyProductInput input, CancellationToken ct = default);
    Task<ShopifyProductOutput> UpdateProductAsync(long productId, ShopifyProductInput input, CancellationToken ct = default);
    Task<ShopifyProductOutput?> GetProductAsync(long productId, CancellationToken ct = default);
    Task<bool> PublishProductAsync(long productId, CancellationToken ct = default);
    Task<bool> DeleteProductAsync(long productId, CancellationToken ct = default);
    Task<bool> SetProductStatusAsync(long productId, string status, CancellationToken ct = default);
    Task<bool> UpdateInventoryAsync(long productId, int quantity, CancellationToken ct = default);

    // Tags / Metafields / Images
    Task AddTagsAsync(long productId, IReadOnlyList<string> tags, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetTagsAsync(long productId, CancellationToken ct = default);
    Task SetMetafieldAsync(long productId, ShopifyMetafield metafield, CancellationToken ct = default);
    Task<ShopifyMetafield?> GetMetafieldAsync(long productId, string ns, string key, CancellationToken ct = default);
    Task<long?> FindProductByMetafieldAsync(string ns, string key, string value, CancellationToken ct = default);
    Task UploadImagesAsync(long productId, IReadOnlyList<string> imageUrls, string? altText, CancellationToken ct = default);

    // Collections
    Task<ShopifyCollection> CreateCollectionAsync(string title, string? description, CancellationToken ct = default);
    Task<IReadOnlyList<ShopifyCollection>> ListCollectionsAsync(CancellationToken ct = default);
    Task AssignToCollectionAsync(long productId, long collectionId, CancellationToken ct = default);

    // Pages
    Task<ShopifyPage> CreatePageAsync(string title, string handle, string bodyHtml, CancellationToken ct = default);
    Task<IReadOnlyList<ShopifyPage>> ListPagesAsync(CancellationToken ct = default);

    // Theme
    Task<ShopifyTheme> UpdateThemeAsync(ShopifyThemeConfig config, CancellationToken ct = default);
    Task<ShopifyThemeConfig> GetThemeConfigAsync(CancellationToken ct = default);

    // Publications / sales channels
    Task PublishToChannelsAsync(long productId, IReadOnlyList<string> channels, CancellationToken ct = default);
}
