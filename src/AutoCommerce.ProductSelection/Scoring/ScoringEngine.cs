using AutoCommerce.Shared.Contracts;

namespace AutoCommerce.ProductSelection.Scoring;

public class ScoringWeights
{
    public double Demand { get; set; } = 0.40;
    public double Competition { get; set; } = 0.10;
    public double Shipping { get; set; } = 0.10;
    public double Margin { get; set; } = 0.40;

    public double Total => Demand + Competition + Shipping + Margin;
}

public interface IScoringEngine
{
    ScoredCandidate Score(ProductCandidate candidate, SelectionConfig config);
}

public class ScoringEngine : IScoringEngine
{
    private readonly ScoringWeights _weights;
    public ScoringEngine(ScoringWeights? weights = null) => _weights = weights ?? new ScoringWeights();

    public ScoredCandidate Score(ProductCandidate c, SelectionConfig cfg)
    {
        var demand = DemandScore(c);
        var competition = CompetitionScore(c);
        var shipping = ShippingScore(c, cfg);
        var margin = MarginScore(c);

        var raw = (demand * _weights.Demand
                     + competition * _weights.Competition
                     + shipping * _weights.Shipping
                     + margin * _weights.Margin) / _weights.Total;

        // Apply a curve so scores spread across the full 0-100 range.
        // A raw 0.40 → ~63, raw 0.55 → ~74, raw 0.65 → ~81, raw 0.75 → ~87, raw 0.85 → ~92
        var curved = Math.Pow(raw, 0.55);
        var score = Math.Round(curved * 100.0, 2);

        var breakdown = new Dictionary<string, double>
        {
            ["demand"] = Math.Round(demand * 100, 2),
            ["competition"] = Math.Round(competition * 100, 2),
            ["shipping"] = Math.Round(shipping * 100, 2),
            ["margin"] = Math.Round(margin * 100, 2)
        };

        string? reject = null;
        if (cfg.MinPrice.HasValue && c.Price < cfg.MinPrice.Value) reject = $"price below min ({c.Price:C} < {cfg.MinPrice.Value:C})";
        else if (cfg.MaxPrice.HasValue && c.Price > cfg.MaxPrice.Value) reject = $"price above max ({c.Price:C} > {cfg.MaxPrice.Value:C})";
        else if (cfg.MaxShippingDays.HasValue && c.ShippingDaysToTarget > cfg.MaxShippingDays.Value) reject = $"shipping too slow ({c.ShippingDaysToTarget} > {cfg.MaxShippingDays.Value} days)";
        else if (c.SupplierCandidates.Count == 0) reject = "no suppliers";
        else if (cfg.MinScore.HasValue && score < cfg.MinScore.Value) reject = $"score below threshold ({score} < {cfg.MinScore.Value})";
        else if (cfg.TargetCategories.Count > 0 &&
                 !cfg.TargetCategories.Any(cat => cat.Equals(c.Category, StringComparison.OrdinalIgnoreCase)))
            reject = "category not targeted";

        return new ScoredCandidate(c, score, breakdown, reject is null, reject);
    }

    internal static double DemandScore(ProductCandidate c)
    {
        // Review cap 500: avg Amazon product ~40 reviews; successful listings 50+.
        var reviews = Normalize(c.ReviewCount, 0, 500);
        var rating = c.Rating <= 0 ? 0 : Math.Clamp((c.Rating - 2.0) / 3.0, 0, 1);
        // Search cap 10 000: 1k/mo is minimum viability, 10k is strong interest.
        var searches = Normalize(c.EstimatedMonthlySearches, 0, 10_000);
        return reviews * 0.35 + rating * 0.35 + searches * 0.30;
    }

    internal static double CompetitionScore(ProductCandidate c)
    {
        var inverted = 1.0 - Normalize(c.CompetitorCount, 0, 100);
        return Math.Clamp(inverted, 0, 1);
    }

    internal static double ShippingScore(ProductCandidate c, SelectionConfig cfg)
    {
        // Unknown/missing shipping → neutral 0.5; typical domestic suppliers deliver in 2-7 days.
        if (c.ShippingDaysToTarget <= 0) return 0.5;
        var cap = Math.Max(cfg.MaxShippingDays ?? 21, 1);
        var ratio = (double)c.ShippingDaysToTarget / cap;
        return Math.Clamp(1.0 - ratio, 0, 1);
    }

    internal static double MarginScore(ProductCandidate c)
    {
        if (c.SupplierCandidates.Count == 0) return 0;
        var cost = c.SupplierCandidates.Min(s => s.Cost);
        if (cost <= 0 || c.Price <= 0) return 0;
        var margin = (double)((c.Price - cost) / c.Price);
        return Math.Clamp(margin, 0, 1);
    }

    private static double Normalize(double value, double min, double max)
    {
        if (max <= min) return 0;
        var clamped = Math.Clamp(value, min, max);
        return (clamped - min) / (max - min);
    }
}
