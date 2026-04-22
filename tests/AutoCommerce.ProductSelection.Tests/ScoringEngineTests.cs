using AutoCommerce.ProductSelection.Scoring;
using AutoCommerce.Shared.Contracts;
using FluentAssertions;
using Xunit;

namespace AutoCommerce.ProductSelection.Tests;

public class ScoringEngineTests
{
    private static SelectionConfig Config(double minScore = 0) => new(
        TargetCategories: new[] { "electronics" },
        MinPrice: 5m, MaxPrice: 500m, MinScore: minScore,
        TopNPerCategory: 3, TargetMarket: "IE", MaxShippingDays: 20);

    private static ProductCandidate Candidate(
        decimal price = 50m,
        decimal cost = 15m,
        int reviews = 2000,
        double rating = 4.6,
        int searches = 20_000,
        int competitors = 200,
        int shippingDays = 10,
        string category = "electronics") => new(
            ExternalId: "t:1", Source: "Test",
            Title: "Widget", Category: category, Description: null,
            ImageUrls: Array.Empty<string>(), Tags: Array.Empty<string>(),
            Price: price, Currency: "USD",
            ReviewCount: reviews, Rating: rating,
            EstimatedMonthlySearches: searches, CompetitorCount: competitors,
            ShippingDaysToTarget: shippingDays,
            SupplierCandidates: new[] { new SupplierListing("s", "x", cost, "USD", shippingDays, rating, 100, null) });

    [Fact]
    public void Strong_Candidate_Scores_Well()
    {
        var engine = new ScoringEngine();
        var s = engine.Score(Candidate(), Config());
        s.Score.Should().BeGreaterThan(55);
        s.Approved.Should().BeTrue();
        s.Breakdown.Should().ContainKeys("demand", "competition", "shipping", "margin");
    }

    [Fact]
    public void Thin_Margin_Scores_Low()
    {
        var engine = new ScoringEngine();
        var s = engine.Score(Candidate(price: 20m, cost: 18m), Config());
        s.Breakdown["margin"].Should().BeLessThan(20);
    }

    [Fact]
    public void Shipping_Beyond_Target_Is_Rejected()
    {
        var engine = new ScoringEngine();
        var s = engine.Score(Candidate(shippingDays: 25), Config());
        s.Approved.Should().BeFalse();
        s.RejectionReason.Should().Contain("shipping");
    }

    [Fact]
    public void Price_Outside_Range_Is_Rejected()
    {
        var engine = new ScoringEngine();
        var tooLow = engine.Score(Candidate(price: 2m), Config());
        var tooHigh = engine.Score(Candidate(price: 1000m), Config());
        tooLow.Approved.Should().BeFalse();
        tooHigh.Approved.Should().BeFalse();
    }

    [Fact]
    public void Category_Not_Targeted_Is_Rejected()
    {
        var engine = new ScoringEngine();
        var s = engine.Score(Candidate(category: "toys"), Config());
        s.Approved.Should().BeFalse();
        s.RejectionReason.Should().Contain("category");
    }

    [Fact]
    public void Score_Below_MinScore_Is_Rejected()
    {
        var engine = new ScoringEngine();
        var s = engine.Score(Candidate(reviews: 10, rating: 3.2, searches: 50, competitors: 950), Config(minScore: 95));
        s.Approved.Should().BeFalse();
    }

    [Fact]
    public void TopNFilter_Takes_Best_Per_Category()
    {
        var engine = new ScoringEngine();
        var cfg = new SelectionConfig(new[] { "electronics", "kitchen" }, 1m, 500m, 0, 2, "IE", 30);
        var scored = new[]
        {
            engine.Score(Candidate(), cfg),
            engine.Score(Candidate() with { }, cfg),
            engine.Score(Candidate(category: "kitchen"), cfg),
            engine.Score(Candidate(category: "kitchen", reviews: 50), cfg),
            engine.Score(Candidate(category: "kitchen", reviews: 10), cfg)
        };
        var filter = new TopNPerCategoryFilter();
        var kept = filter.Filter(scored, cfg);
        kept.Count(x => x.Candidate.Category == "kitchen").Should().BeLessThanOrEqualTo(2);
    }
}
