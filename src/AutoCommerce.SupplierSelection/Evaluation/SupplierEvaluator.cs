using AutoCommerce.Shared.Contracts;
using AutoCommerce.SupplierSelection.Domain;

namespace AutoCommerce.SupplierSelection.Evaluation;

public class EvaluationWeights
{
    public double Price { get; set; } = 0.30;
    public double Rating { get; set; } = 0.15;
    public double Shipping { get; set; } = 0.25;
    public double Stock { get; set; } = 0.10;
    public double Reliability { get; set; } = 0.20;

    public double Total => Price + Rating + Shipping + Stock + Reliability;
}

public interface ISupplierEvaluator
{
    IReadOnlyList<SupplierEvaluation> Evaluate(
        IReadOnlyList<SupplierListing> listings,
        SupplierSelectionOptions options);
}

public class SupplierEvaluator : ISupplierEvaluator
{
    private readonly ISupplierCatalog _catalog;
    private readonly EvaluationWeights _weights;

    public SupplierEvaluator(ISupplierCatalog catalog, EvaluationWeights? weights = null)
    {
        _catalog = catalog;
        _weights = weights ?? new EvaluationWeights();
    }

    public IReadOnlyList<SupplierEvaluation> Evaluate(
        IReadOnlyList<SupplierListing> listings,
        SupplierSelectionOptions options)
    {
        if (listings.Count == 0) return Array.Empty<SupplierEvaluation>();

        var minCost = listings.Min(l => l.Cost);
        var maxCost = listings.Max(l => l.Cost);

        return listings.Select(l => ScoreOne(l, options, minCost, maxCost)).ToList();
    }

    private SupplierEvaluation ScoreOne(
        SupplierListing listing,
        SupplierSelectionOptions options,
        decimal minCost,
        decimal maxCost)
    {
        var price = PriceScore(listing.Cost, minCost, maxCost);
        var rating = RatingScore(listing.Rating);
        var shipping = ShippingScore(listing.ShippingDays, options.MaxShippingDays);
        var stock = StockScore(listing.StockAvailable, options.MinStock);
        var reliability = ReliabilityScore(listing.SupplierKey);

        var raw = (price * _weights.Price
                 + rating * _weights.Rating
                 + shipping * _weights.Shipping
                 + stock * _weights.Stock
                 + reliability * _weights.Reliability) / _weights.Total;
        var score = Math.Round(raw * 100.0, 2);

        string? reject = null;
        if (listing.Cost <= 0) reject = "invalid cost";
        else if (listing.ShippingDays > options.MaxShippingDays) reject = $"shipping {listing.ShippingDays}d exceeds max {options.MaxShippingDays}d";
        else if (listing.StockAvailable < options.MinStock) reject = $"stock {listing.StockAvailable} below min {options.MinStock}";
        else if (score < options.MinScore) reject = $"score {score} below min {options.MinScore}";

        return new SupplierEvaluation(
            listing.SupplierKey,
            score,
            Math.Round(price * 100, 2),
            Math.Round(rating * 100, 2),
            Math.Round(shipping * 100, 2),
            Math.Round(stock * 100, 2),
            Math.Round(reliability * 100, 2),
            listing.Cost,
            listing.Currency,
            listing.ShippingDays,
            listing.StockAvailable,
            reject is null,
            reject);
    }

    internal static double PriceScore(decimal cost, decimal min, decimal max)
    {
        if (cost <= 0) return 0;
        if (max <= min) return 1.0;
        var span = (double)(max - min);
        var offset = (double)(cost - min);
        return Math.Clamp(1.0 - (offset / span), 0, 1);
    }

    internal static double RatingScore(double rating)
    {
        if (rating <= 0) return 0.3;
        return Math.Clamp((rating - 2.5) / 2.5, 0, 1);
    }

    internal static double ShippingScore(int shippingDays, int maxDays)
    {
        if (shippingDays <= 0) return 0.4;
        if (maxDays <= 0) return 0.5;
        if (shippingDays > maxDays) return 0;
        return Math.Clamp(1.0 - ((double)shippingDays / maxDays), 0, 1);
    }

    internal static double StockScore(int stock, int minStock)
    {
        if (stock <= 0) return 0;
        if (stock < minStock) return 0.3;
        if (stock >= 500) return 1.0;
        return Math.Clamp(stock / 500.0, 0.3, 1.0);
    }

    private double ReliabilityScore(string supplierKey)
    {
        var profile = _catalog.Get(supplierKey);
        return profile?.BaseReliability ?? 0.5;
    }
}
