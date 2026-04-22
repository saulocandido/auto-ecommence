using System.Net.Http.Json;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;
using FluentAssertions;
using Xunit;

namespace AutoCommerce.Brain.Tests;

public class OrdersApiTests : IClassFixture<BrainFactory>
{
    private readonly BrainFactory _factory;
    public OrdersApiTests(BrainFactory f) => _factory = f;

    [Fact]
    public async Task End_To_End_Product_Order_Tracking_Workflow()
    {
        var client = _factory.CreateAuthedClient();
        var opts = DomainEvent.SerializerOptions;

        var import = new ProductImportDto(
            ExternalId: "e2e:widget",
            Title: "E2E Widget",
            Category: "electronics",
            Description: null,
            ImageUrls: new[] { "https://cdn.example.com/w.jpg" },
            Tags: new[] { "e2e" },
            TargetMarket: "IE",
            Score: 80,
            Suppliers: new[] { new SupplierListing("ali-a", "EXT-W", 10m, "USD", 10, 4.5, 100, null) });
        var productResp = await client.PostAsJsonAsync("/api/products/import", import, opts);
        var product = await productResp.Content.ReadFromJsonAsync<ProductResponse>(opts);
        product!.Price.Should().BeGreaterThan(0);

        var order = new OrderCreateDto("SHOP-1", "a@b.com", "Alice", "IE",
            new[] { new OrderLineDto(product.Id, 2, product.Price!.Value) });
        var orderResp = await client.PostAsJsonAsync("/api/orders", order, opts);
        var created = await orderResp.Content.ReadFromJsonAsync<OrderResponse>(opts);
        created!.Status.Should().Be("Pending");
        created.Total.Should().Be(product.Price.Value * 2);

        var track = await client.PatchAsJsonAsync($"/api/orders/{created.Id}/tracking",
            new OrderTrackingUpdateDto("Fulfilled", "TRK123", "https://track/abc", "SUP-1"), opts);
        track.EnsureSuccessStatusCode();
        var updated = await track.Content.ReadFromJsonAsync<OrderResponse>(opts);
        updated!.Status.Should().Be("Fulfilled");
        updated.TrackingNumber.Should().Be("TRK123");
    }

    [Fact]
    public async Task Duplicate_Shop_Order_Is_Idempotent()
    {
        var client = _factory.CreateAuthedClient();
        var opts = DomainEvent.SerializerOptions;

        var import = new ProductImportDto("dupe:w", "W", "kitchen", null,
            new[] { "x" }, new[] { "t" }, "IE", 75,
            new[] { new SupplierListing("ali-a", "E", 5m, "USD", 10, 4.5, 50, null) });
        var p = (await (await client.PostAsJsonAsync("/api/products/import", import, opts))
            .Content.ReadFromJsonAsync<ProductResponse>(opts))!;

        var dto = new OrderCreateDto("DUPE-1", "a@b.com", null, "IE",
            new[] { new OrderLineDto(p.Id, 1, p.Price!.Value) });

        var r1 = await client.PostAsJsonAsync("/api/orders", dto, opts);
        var r2 = await client.PostAsJsonAsync("/api/orders", dto, opts);
        r1.EnsureSuccessStatusCode();
        r2.EnsureSuccessStatusCode();
        var o1 = await r1.Content.ReadFromJsonAsync<OrderResponse>(opts);
        var o2 = await r2.Content.ReadFromJsonAsync<OrderResponse>(opts);
        o1!.Id.Should().Be(o2!.Id);
    }
}
