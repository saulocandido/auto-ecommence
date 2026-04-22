using AutoCommerce.Shared.Contracts;
using AutoCommerce.SupplierSelection.Domain;
using AutoCommerce.SupplierSelection.Evaluation;
using FluentAssertions;

namespace AutoCommerce.SupplierSelection.Tests;

public class SupplierEvaluatorTests
{
    private static SupplierEvaluator Evaluator() => new(new StaticSupplierCatalog());

    private static SupplierSelectionOptions DefaultOptions() =>
        new(MinScore: 40, MaxShippingDays: 21, MinStock: 10, TargetMarket: "IE");

    private static SupplierListing Listing(
        string key = "cjdropshipping",
        decimal cost = 12m,
        int shippingDays = 10,
        double rating = 4.6,
        int stock = 200) =>
        new(key, $"ext-{key}", cost, "USD", shippingDays, rating, stock, null);

    [Fact]
    public void Evaluate_EmptyListings_ReturnsEmpty()
    {
        var result = Evaluator().Evaluate(Array.Empty<SupplierListing>(), DefaultOptions());
        result.Should().BeEmpty();
    }

    [Fact]
    public void PriceScore_CheapestListing_ReceivesMaxPriceScore()
    {
        var listings = new[]
        {
            Listing("aliexpress", cost: 8m),
            Listing("cjdropshipping", cost: 14m),
            Listing("spocket", cost: 22m)
        };
        var evals = Evaluator().Evaluate(listings, DefaultOptions());
        evals.Single(e => e.SupplierKey == "aliexpress").PriceScore.Should().Be(100);
        evals.Single(e => e.SupplierKey == "spocket").PriceScore.Should().Be(0);
    }

    [Fact]
    public void ShippingScore_ExceedsMaxDays_IsZeroAndRejected()
    {
        var opts = DefaultOptions() with { MaxShippingDays = 10 };
        var listings = new[] { Listing(shippingDays: 25) };
        var evals = Evaluator().Evaluate(listings, opts);
        evals.Single().ShippingScore.Should().Be(0);
        evals.Single().Viable.Should().BeFalse();
        evals.Single().RejectionReason.Should().Contain("shipping");
    }

    [Fact]
    public void StockBelowMinimum_MarksNonViable()
    {
        var opts = DefaultOptions() with { MinStock = 50 };
        var listings = new[] { Listing(stock: 5) };
        var evals = Evaluator().Evaluate(listings, opts);
        evals.Single().Viable.Should().BeFalse();
        evals.Single().RejectionReason.Should().Contain("stock");
    }

    [Fact]
    public void ReliabilityFromCatalog_RaisesScore()
    {
        var listings = new[]
        {
            Listing("unknown-supplier", cost: 10m, shippingDays: 10, rating: 4.6, stock: 200),
            Listing("amazon-prime",     cost: 10m, shippingDays: 10, rating: 4.6, stock: 200)
        };
        var evals = Evaluator().Evaluate(listings, DefaultOptions());
        var unknown = evals.Single(e => e.SupplierKey == "unknown-supplier");
        var amazon = evals.Single(e => e.SupplierKey == "amazon-prime");
        amazon.ReliabilityScore.Should().BeGreaterThan(unknown.ReliabilityScore);
        amazon.Score.Should().BeGreaterThan(unknown.Score);
    }

    [Fact]
    public void ScoreBelowMinScore_IsRejected()
    {
        var opts = DefaultOptions() with { MinScore = 95 };
        var listings = new[] { Listing(cost: 50m, rating: 3.0, stock: 10, shippingDays: 19) };
        var evals = Evaluator().Evaluate(listings, opts);
        evals.Single().Viable.Should().BeFalse();
        evals.Single().RejectionReason.Should().Contain("score");
    }

    [Fact]
    public void ViableSupplier_ProducesAllComponentScoresInRange()
    {
        var listings = new[] { Listing() };
        var eval = Evaluator().Evaluate(listings, DefaultOptions()).Single();
        eval.Viable.Should().BeTrue();
        eval.Score.Should().BeInRange(0, 100);
        eval.PriceScore.Should().BeInRange(0, 100);
        eval.RatingScore.Should().BeInRange(0, 100);
        eval.ShippingScore.Should().BeInRange(0, 100);
        eval.StockScore.Should().BeInRange(0, 100);
        eval.ReliabilityScore.Should().BeInRange(0, 100);
    }
}
