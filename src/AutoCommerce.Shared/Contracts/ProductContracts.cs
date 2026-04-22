namespace AutoCommerce.Shared.Contracts;

public enum ProductStatus
{
    Draft,
    Active,
    Paused,
    Killed
}

public sealed record SupplierListing(
    string SupplierKey,
    string ExternalProductId,
    decimal Cost,
    string Currency,
    int ShippingDays,
    double Rating,
    int StockAvailable,
    string? Url);

public sealed record ProductImportDto(
    string ExternalId,
    string Title,
    string Category,
    string? Description,
    IReadOnlyList<string> ImageUrls,
    IReadOnlyList<string> Tags,
    string TargetMarket,
    double Score,
    IReadOnlyList<SupplierListing> Suppliers,
    IReadOnlyDictionary<string, double>? ScoreBreakdown = null);

public sealed record ProductResponse(
    Guid Id,
    string ExternalId,
    string Title,
    string Category,
    string? Description,
    IReadOnlyList<string> ImageUrls,
    IReadOnlyList<string> Tags,
    string TargetMarket,
    double Score,
    decimal? Cost,
    decimal? Price,
    double? MarginPercent,
    string Status,
    string? SupplierKey,
    IReadOnlyList<SupplierListing> Suppliers,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProductUpdateDto(
    string? Title,
    string? Description,
    string? Status,
    string? SupplierKey,
    decimal? Cost,
    decimal? Price);
