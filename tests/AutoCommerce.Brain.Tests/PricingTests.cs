using System.Net.Http.Json;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;
using FluentAssertions;
using Xunit;

namespace AutoCommerce.Brain.Tests;

public class PricingTests : IClassFixture<BrainFactory>
{
    private readonly BrainFactory _factory;
    public PricingTests(BrainFactory f) => _factory = f;

    [Fact]
    public async Task Custom_Rule_Applies_To_Category()
    {
        var client = _factory.CreateAuthedClient();
        var opts = DomainEvent.SerializerOptions;

        var rule = new PricingRuleDto("kitchen", 3.0, 30.0, 5m, 200m);
        var put = await client.PutAsJsonAsync("/api/pricing/rules", rule, opts);
        put.EnsureSuccessStatusCode();

        var import = new ProductImportDto("rule:k", "Kitchen Thing", "kitchen", null,
            new[] { "i" }, new[] { "t" }, "IE", 70,
            new[] { new SupplierListing("a", "X", 20m, "USD", 10, 4.5, 100, null) });
        var resp = await client.PostAsJsonAsync("/api/products/import", import, opts);
        var product = await resp.Content.ReadFromJsonAsync<ProductResponse>(opts);
        product!.Price.Should().Be(60m);
        product.MarginPercent.Should().BeInRange(60.0, 70.0);
    }

    [Fact]
    public async Task Low_Margin_Pauses_Product()
    {
        var client = _factory.CreateAuthedClient();
        var opts = DomainEvent.SerializerOptions;

        var rule = new PricingRuleDto("fitness", 1.05, 50.0, 1m, 500m);
        await client.PutAsJsonAsync("/api/pricing/rules", rule, opts);

        var import = new ProductImportDto("rule:low", "Thin", "fitness", null,
            new[] { "i" }, new[] { "t" }, "IE", 70,
            new[] { new SupplierListing("a", "X", 40m, "USD", 10, 4.5, 100, null) });
        var resp = await client.PostAsJsonAsync("/api/products/import", import, opts);
        var product = await resp.Content.ReadFromJsonAsync<ProductResponse>(opts);
        product!.Status.Should().Be("Paused");
    }
}
