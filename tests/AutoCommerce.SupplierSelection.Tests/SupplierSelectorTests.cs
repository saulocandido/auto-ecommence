using AutoCommerce.Shared.Contracts;
using AutoCommerce.SupplierSelection.Domain;
using AutoCommerce.SupplierSelection.Evaluation;
using FluentAssertions;

namespace AutoCommerce.SupplierSelection.Tests;

public class SupplierSelectorTests
{
    private static SupplierSelector Build() =>
        new(new SupplierEvaluator(new StaticSupplierCatalog()));

    private static SupplierSelectionOptions DefaultOptions() =>
        new(MinScore: 40, MaxShippingDays: 21, MinStock: 10, TargetMarket: "IE");

    [Fact]
    public void Select_EmptyListings_ReturnsNoSupplier()
    {
        var result = Build().Select(Guid.NewGuid(), "ext-x",
            Array.Empty<SupplierListing>(), DefaultOptions());

        result.ChosenSupplierKey.Should().BeNull();
        result.RejectionReason.Should().Be("no supplier listings");
    }

    [Fact]
    public void Select_PicksHighestViableScore()
    {
        var listings = new[]
        {
            new SupplierListing("aliexpress",     "a1",  9m, "USD", 18, 4.2, 200, null),
            new SupplierListing("spocket",        "s1", 18m, "USD",  4, 4.8, 150, null),
            new SupplierListing("amazon-prime",   "am1", 15m, "USD",  2, 4.9, 500, null)
        };
        var result = Build().Select(Guid.NewGuid(), "ext-1", listings, DefaultOptions());

        result.ChosenSupplierKey.Should().Be("amazon-prime");
        result.Score.Should().BeGreaterThan(result.Evaluations[1].Score - 0.01);
        result.Evaluations.Should().HaveCount(3);
    }

    [Fact]
    public void Select_AllNonViable_ReturnsRejection()
    {
        var opts = DefaultOptions() with { MaxShippingDays = 5 };
        var listings = new[]
        {
            new SupplierListing("aliexpress", "a1", 9m, "USD", 20, 4.2, 200, null),
            new SupplierListing("cjdropshipping", "c1", 12m, "USD", 18, 4.5, 300, null)
        };
        var result = Build().Select(Guid.NewGuid(), "ext-2", listings, opts);

        result.ChosenSupplierKey.Should().BeNull();
        result.Evaluations.Should().HaveCount(2);
        result.Evaluations.Should().OnlyContain(e => !e.Viable);
        result.RejectionReason.Should().NotBeNull();
    }

    [Fact]
    public void Select_SkipsNonViableEvenIfHighestRaw()
    {
        var opts = DefaultOptions() with { MinStock = 100 };
        var listings = new[]
        {
            new SupplierListing("spocket",     "s1",  8m, "USD", 3, 5.0,  20, null),  // cheap/fast/top rating, but low stock
            new SupplierListing("zendrop",     "z1", 14m, "USD", 7, 4.7, 300, null)
        };
        var result = Build().Select(Guid.NewGuid(), "ext-3", listings, opts);
        result.ChosenSupplierKey.Should().Be("zendrop");
    }
}
