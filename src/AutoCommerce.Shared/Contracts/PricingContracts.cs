namespace AutoCommerce.Shared.Contracts;

public sealed record PricingRuleDto(
    string Category,
    double MarkupMultiplier,
    double MinMarginPercent,
    decimal MinPrice,
    decimal MaxPrice);

public sealed record PriceUpdateDto(
    Guid ProductId,
    decimal NewCost,
    decimal NewPrice,
    double MarginPercent);

public sealed record DashboardMetrics(
    int TotalProducts,
    int ActiveProducts,
    int PausedProducts,
    int TotalOrders,
    int PendingOrders,
    int FulfilledOrders,
    decimal RevenueLast24h,
    decimal ProfitLast24h,
    double AvgMarginPercent,
    IReadOnlyList<TopProduct> TopProducts,
    IReadOnlyList<RecentEvent> RecentEvents);

public sealed record TopProduct(Guid Id, string Title, int OrderCount, decimal Revenue);

public sealed record RecentEvent(Guid Id, string Type, string Source, DateTimeOffset OccurredAt);

public sealed record RecentEventWithPayload(
    Guid Id,
    string Type,
    string Source,
    DateTimeOffset OccurredAt,
    string PayloadJson);
