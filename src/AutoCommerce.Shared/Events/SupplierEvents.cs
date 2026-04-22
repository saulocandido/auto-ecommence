namespace AutoCommerce.Shared.Events;

public sealed record SupplierSelectedPayload(
    Guid ProductId,
    string ExternalId,
    string SupplierKey,
    decimal Cost,
    string Currency,
    double Score,
    DateTimeOffset SelectedAt);

public sealed record SupplierPriceChangedPayload(
    Guid ProductId,
    string SupplierKey,
    decimal OldCost,
    decimal NewCost,
    string Currency,
    DateTimeOffset ChangedAt);

public sealed record SupplierStockChangedPayload(
    Guid ProductId,
    string SupplierKey,
    int OldStock,
    int NewStock,
    DateTimeOffset ChangedAt);

public sealed record OrderSentToSupplierPayload(
    Guid OrderId,
    string ShopOrderId,
    string SupplierKey,
    string SupplierOrderId,
    string? TrackingNumber,
    string? TrackingUrl,
    DateTimeOffset? EstimatedDelivery);
