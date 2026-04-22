namespace AutoCommerce.Shared.Contracts;

public sealed record SupplierProfile(
    string SupplierKey,
    string Name,
    string Region,
    double BaseReliability,
    string? Notes = null);

public sealed record SupplierEvaluation(
    string SupplierKey,
    double Score,
    double PriceScore,
    double RatingScore,
    double ShippingScore,
    double StockScore,
    double ReliabilityScore,
    decimal Cost,
    string Currency,
    int ShippingDays,
    int StockAvailable,
    bool Viable,
    string? RejectionReason);

public sealed record SupplierSelectionResult(
    Guid ProductId,
    string ExternalId,
    string? ChosenSupplierKey,
    decimal? ChosenCost,
    string? Currency,
    double? Score,
    IReadOnlyList<SupplierEvaluation> Evaluations,
    string? RejectionReason);

public sealed record SupplierAssignmentRequest(
    string SupplierKey,
    decimal Cost,
    double Score,
    string? Currency = null);

public sealed record FulfillmentRequest(
    Guid OrderId,
    string ShopOrderId,
    Guid ProductId,
    string? SupplierKey,
    int Quantity,
    decimal UnitPrice,
    string ShippingCountry,
    string CustomerEmail);

public sealed record FulfillmentResult(
    bool Success,
    string? SupplierOrderId,
    string? TrackingNumber,
    string? TrackingUrl,
    DateTimeOffset? EstimatedDelivery,
    string? Reason);

public sealed record SupplierSelectionOptions(
    double MinScore,
    int MaxShippingDays,
    int MinStock,
    string TargetMarket);
