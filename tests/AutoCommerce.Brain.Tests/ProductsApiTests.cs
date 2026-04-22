using System.Net;
using System.Net.Http.Json;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;
using FluentAssertions;
using Xunit;

namespace AutoCommerce.Brain.Tests;

public class ProductsApiTests : IClassFixture<BrainFactory>
{
    private readonly BrainFactory _factory;
    public ProductsApiTests(BrainFactory f) => _factory = f;

    private static ProductImportDto Sample(string externalId = "test:1") => new(
        ExternalId: externalId,
        Title: "Test Mini Projector",
        Category: "electronics",
        Description: "A test product",
        ImageUrls: new[] { "https://cdn.example.com/a.jpg" },
        Tags: new[] { "test" },
        TargetMarket: "IE",
        Score: 78.0,
        Suppliers: new[]
        {
            new SupplierListing("ali-a", "EXT-1", 22.50m, "USD", 10, 4.6, 150, null)
        });

    [Fact]
    public async Task Import_Then_Get_Returns_Product_With_Price_Applied()
    {
        var client = _factory.CreateAuthedClient();
        var create = await client.PostAsJsonAsync("/api/products/import", Sample(), DomainEvent.SerializerOptions);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var product = await create.Content.ReadFromJsonAsync<ProductResponse>(DomainEvent.SerializerOptions);
        product!.Cost.Should().Be(22.50m);
        product.Price!.Value.Should().BeGreaterThan(product.Cost!.Value);
        product.MarginPercent!.Value.Should().BeGreaterThan(0);

        var fetched = await client.GetFromJsonAsync<ProductResponse>($"/api/products/{product.Id}", DomainEvent.SerializerOptions);
        fetched!.ExternalId.Should().Be("test:1");
        fetched.Status.Should().BeOneOf("Active", "Draft");
    }

    [Fact]
    public async Task Unauthenticated_Returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/products");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Import_Is_Idempotent_By_ExternalId()
    {
        var client = _factory.CreateAuthedClient();
        var first = await client.PostAsJsonAsync("/api/products/import", Sample("test:idempotent"), DomainEvent.SerializerOptions);
        var second = await client.PostAsJsonAsync("/api/products/import", Sample("test:idempotent"), DomainEvent.SerializerOptions);
        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        var p1 = await first.Content.ReadFromJsonAsync<ProductResponse>(DomainEvent.SerializerOptions);
        var p2 = await second.Content.ReadFromJsonAsync<ProductResponse>(DomainEvent.SerializerOptions);
        p1!.Id.Should().Be(p2!.Id);
    }

    [Fact]
    public async Task Update_Status_To_Paused_Succeeds()
    {
        var client = _factory.CreateAuthedClient();
        var created = await client.PostAsJsonAsync("/api/products/import", Sample("test:pause"), DomainEvent.SerializerOptions);
        var product = await created.Content.ReadFromJsonAsync<ProductResponse>(DomainEvent.SerializerOptions);
        var patched = await client.PatchAsJsonAsync($"/api/products/{product!.Id}",
            new ProductUpdateDto(null, null, "Paused", null, null, null), DomainEvent.SerializerOptions);
        patched.EnsureSuccessStatusCode();
        var updated = await patched.Content.ReadFromJsonAsync<ProductResponse>(DomainEvent.SerializerOptions);
        updated!.Status.Should().Be("Paused");
    }
}
