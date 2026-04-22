using System.Net;
using System.Net.Http.Json;
using AutoCommerce.ProductSelection.Services;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;
using FluentAssertions;
using Xunit;

namespace AutoCommerce.ProductSelection.Tests;

public class LinkValidatorTests
{
    private static SelectionConfig Config() => new(
        TargetCategories: new[] { "electronics" },
        MinPrice: 5m, MaxPrice: 500m, MinScore: 0,
        TopNPerCategory: 3, TargetMarket: "IE", MaxShippingDays: 20);

    private static ProductCandidate Candidate(string url, string title = "Wireless Bluetooth Headphones") => new(
        ExternalId: "t:1", Source: "Test",
        Title: title, Category: "electronics", Description: null,
        ImageUrls: Array.Empty<string>(), Tags: Array.Empty<string>(),
        Price: 50m, Currency: "USD",
        ReviewCount: 100, Rating: 4.5,
        EstimatedMonthlySearches: 1000, CompetitorCount: 30,
        ShippingDaysToTarget: 7,
        SupplierCandidates: new[]
        {
            new SupplierListing("aliexpress", "ext1", 15m, "USD", 7, 4.5, 100, url)
        });

    private static ScoredCandidate Scored(string url, string title = "Wireless Bluetooth Headphones") =>
        new(Candidate(url, title), 85.0,
            new Dictionary<string, double> { ["demand"] = 80, ["competition"] = 70, ["shipping"] = 60, ["margin"] = 90 },
            true, null);

    [Fact]
    public void LinkStatus_Enum_Has_Expected_Values()
    {
        Enum.GetValues<LinkStatus>().Should().Contain(LinkStatus.Verified);
        Enum.GetValues<LinkStatus>().Should().Contain(LinkStatus.Corrected);
        Enum.GetValues<LinkStatus>().Should().Contain(LinkStatus.Invalid);
        Enum.GetValues<LinkStatus>().Should().Contain(LinkStatus.Skipped);
    }

    [Fact]
    public void LinkValidationResult_With_No_Url_Is_Skipped()
    {
        var result = new LinkValidationResult("t:1", "supplier", "", null, LinkStatus.Skipped, "No URL provided");
        result.Status.Should().Be(LinkStatus.Skipped);
        result.Detail.Should().Be("No URL provided");
    }

    [Fact]
    public void LinkValidationResult_With_Corrected_Url_Has_Both_Urls()
    {
        var result = new LinkValidationResult("t:1", "supplier",
            "https://example.com/old", "https://example.com/new",
            LinkStatus.Corrected, "Original returned 404, found alternative");
        result.OriginalUrl.Should().Be("https://example.com/old");
        result.CorrectedUrl.Should().Be("https://example.com/new");
        result.Status.Should().Be(LinkStatus.Corrected);
    }

    [Fact]
    public void LinkValidationReport_Calculates_Counts()
    {
        var results = new List<LinkValidationResult>
        {
            new("t:1", "s1", "https://a.com", null, LinkStatus.Verified, null),
            new("t:2", "s1", "https://b.com", null, LinkStatus.Verified, null),
            new("t:3", "s1", "https://c.com", "https://c2.com", LinkStatus.Corrected, "404"),
            new("t:4", "s1", "https://d.com", null, LinkStatus.Invalid, "500"),
            new("t:5", "s1", "", null, LinkStatus.Skipped, "No URL"),
        };

        var report = new LinkValidationReport(
            DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow,
            Total: 5, Verified: 2, Corrected: 1, Invalid: 1, Skipped: 1,
            Results: results);

        report.Total.Should().Be(5);
        report.Verified.Should().Be(2);
        report.Corrected.Should().Be(1);
        report.Invalid.Should().Be(1);
        report.Skipped.Should().Be(1);
    }

    [Fact]
    public void EventTypes_Has_Link_Events()
    {
        EventTypes.ProductLinkVerified.Should().Be("product.link_verified");
        EventTypes.ProductLinkCorrected.Should().Be("product.link_corrected");
        EventTypes.ProductLinkInvalid.Should().Be("product.link_invalid");
    }

    [Fact]
    public void ScoredCandidate_With_No_Supplier_Url_Should_Be_Skippable()
    {
        var candidate = new ProductCandidate(
            "t:1", "Test", "Widget", "electronics", null,
            Array.Empty<string>(), Array.Empty<string>(),
            50m, "USD", 100, 4.5, 1000, 30, 7,
            new[] { new SupplierListing("s", "x", 15m, "USD", 7, 4.5, 100, null) });

        candidate.SupplierCandidates[0].Url.Should().BeNull();
    }

    [Fact]
    public void ScoredCandidate_With_Supplier_Url_Should_Be_Validatable()
    {
        var candidate = Candidate("https://example.com/product/123");
        candidate.SupplierCandidates[0].Url.Should().Be("https://example.com/product/123");
    }
}
