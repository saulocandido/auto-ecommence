using Xunit;
using FluentAssertions;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.StoreManagement.Services;

namespace AutoCommerce.StoreManagement.Tests;

public class ShopifyControllerTests : IAsyncLifetime
{
    private StoreApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new StoreApplicationFactory();
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private StubBrainClient Brain => (StubBrainClient)_factory.Services.GetRequiredService<IBrainClient>();

    private static StringContent Json(object o) =>
        new(JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");

    private ProductResponse MakeProduct(Guid id, decimal price = 19.99m, int stock = 10, string status = "Active")
    {
        var supplier = new SupplierListing("s1", "ext-1", 5m, "USD", 5, 4.5, stock, "http://supplier");
        return new ProductResponse(
            id, "ext-1", "Widget", "General", "A widget",
            new[] { "http://img/1.jpg" }, new[] { "gadget" },
            "US", 0.9, 5m, price, 50, status, "s1",
            new[] { supplier }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task SyncProduct_ReturnsOk_WithResult()
    {
        var id = Guid.NewGuid();
        Brain.Products[id] = MakeProduct(id);

        var resp = await _client.PostAsync("/api/shopify/sync-product", Json(new { brainProductId = id }));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("shopifyProductId");
    }

    [Fact]
    public async Task SyncProduct_MissingInBrain_Returns400()
    {
        var resp = await _client.PostAsync("/api/shopify/sync-product", Json(new { brainProductId = Guid.NewGuid() }));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BulkSync_ProcessesAllProducts()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Brain.Products[a] = MakeProduct(a);
        Brain.Products[b] = MakeProduct(b);

        var resp = await _client.PostAsync("/api/shopify/sync-products/bulk", Json(new { brainProductIds = (List<Guid>?)null }));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("\"count\":2");
    }

    [Fact]
    public async Task SyncStatus_AfterSync_ReturnsRow()
    {
        var id = Guid.NewGuid();
        Brain.Products[id] = MakeProduct(id);
        await _client.PostAsync("/api/shopify/sync-product", Json(new { brainProductId = id }));

        var resp = await _client.GetAsync($"/api/shopify/sync-status/{id}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("shopifyProductId");
    }

    [Fact]
    public async Task Health_ReturnsConnectedTrue()
    {
        var resp = await _client.GetAsync("/api/shopify/health");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("\"connected\":true");
    }

    [Fact]
    public async Task AdminConfig_GetThenPut_RoundTrip()
    {
        var get = await _client.GetAsync("/api/shopify/admin/config");
        get.StatusCode.Should().Be(HttpStatusCode.OK);

        var put = await _client.PutAsync("/api/shopify/admin/config", Json(new
        {
            shopDomain = "test.myshopify.com",
            webhookSecret = "s3cret",
            autoArchiveOnZeroStock = false,
            managedTag = "mine"
        }));
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await put.Content.ReadAsStringAsync();
        body.Should().Contain("test.myshopify.com");
        body.Should().Contain("\"hasWebhookSecret\":true");
        body.Should().Contain("\"autoArchiveOnZeroStock\":false");
        // secret never returned
        body.Should().NotContain("s3cret");
    }

    [Fact]
    public async Task Webhook_ProductsUpdate_WithoutSecret_Accepted()
    {
        // No secret configured -> dev mode accepts unverified
        var payload = JsonSerializer.Serialize(new { id = 999L, updated_at = DateTimeOffset.UtcNow });
        var resp = await _client.PostAsync("/api/shopify/webhooks/products/update",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Webhook_ProductsUpdate_WithSecretButNoHeader_Unauthorized()
    {
        await _client.PutAsync("/api/shopify/admin/config", Json(new { webhookSecret = "abc" }));
        var payload = JsonSerializer.Serialize(new { id = 1L, updated_at = DateTimeOffset.UtcNow });
        var resp = await _client.PostAsync("/api/shopify/webhooks/products/update",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
