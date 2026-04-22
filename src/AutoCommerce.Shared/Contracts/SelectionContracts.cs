namespace AutoCommerce.Shared.Contracts;

public sealed record SelectionConfig(
    IReadOnlyList<string> TargetCategories,
    decimal? MinPrice,
    decimal? MaxPrice,
    double? MinScore,
    int TopNPerCategory,
    string TargetMarket,
    int? MaxShippingDays);

public sealed record ProductCandidate(
    string ExternalId,
    string Source,
    string Title,
    string Category,
    string? Description,
    IReadOnlyList<string> ImageUrls,
    IReadOnlyList<string> Tags,
    decimal Price,
    string Currency,
    int ReviewCount,
    double Rating,
    int EstimatedMonthlySearches,
    int CompetitorCount,
    int ShippingDaysToTarget,
    IReadOnlyList<SupplierListing> SupplierCandidates);

public sealed record ScoredCandidate(
    ProductCandidate Candidate,
    double Score,
    IReadOnlyDictionary<string, double> Breakdown,
    bool Approved,
    string? RejectionReason);

public sealed record RecommendationResponse(
    DateTimeOffset GeneratedAt,
    SelectionConfig Config,
    IReadOnlyList<ScoredCandidate> Recommendations);
