using Xunit;
using FluentAssertions;
using System.Net;
using System.Text.Json;

namespace AutoCommerce.StoreManagement.Tests;

public class StoreControllerTests : IAsyncLifetime
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

    private static StringContent Json(object o) =>
        new(JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task InitializeStore_ReturnsOk()
    {
        var response = await _client.PostAsync("/api/store/initialize", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"success\":true");
    }

    [Fact]
    public async Task SyncProduct_WithValidRequest_ReturnsOk()
    {
        var response = await _client.PostAsync("/api/store/sync-product", Json(new
        {
            brainProductId = Guid.NewGuid(),
            title = "Test",
            description = "D",
            price = 99.99m,
            imageUrl = (string?)null
        }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SyncPrice_AfterSync_ReturnsOk()
    {
        var id = Guid.NewGuid();
        await _client.PostAsync("/api/store/sync-product", Json(new
        {
            brainProductId = id, title = "T", description = "D", price = 10m, imageUrl = (string?)null
        }));

        var resp = await _client.PostAsync("/api/store/sync-price", Json(new { brainProductId = id, newPrice = 25m }));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SyncStatus_AfterSync_ReturnsOk()
    {
        var id = Guid.NewGuid();
        await _client.PostAsync("/api/store/sync-product", Json(new
        {
            brainProductId = id, title = "T", description = "D", price = 10m, imageUrl = (string?)null
        }));

        var resp = await _client.PostAsync("/api/store/sync-status", Json(new { brainProductId = id, status = "archived" }));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SyncStock_AfterSync_ReturnsOk()
    {
        var id = Guid.NewGuid();
        await _client.PostAsync("/api/store/sync-product", Json(new
        {
            brainProductId = id, title = "T", description = "D", price = 10m, imageUrl = (string?)null
        }));

        var resp = await _client.PostAsync("/api/store/sync-stock", Json(new { brainProductId = id, quantity = 42 }));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetTheme_ReturnsDefault()
    {
        var resp = await _client.GetAsync("/api/store/theme");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateTheme_ReturnsOk()
    {
        var resp = await _client.PutAsync("/api/store/theme", Json(new
        {
            themeName = "Custom",
            homepageHeading = "Welcome",
            homepageSubheading = "Shop the collection",
            primaryColor = "#123456",
            logoUrl = (string?)null
        }));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("\"success\":true");
    }

    [Fact]
    public async Task ListPages_AfterInitialize_ReturnsLegalPages()
    {
        await _client.PostAsync("/api/store/initialize", null);
        var resp = await _client.GetAsync("/api/store/pages");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("privacy-policy");
        body.Should().Contain("terms-of-service");
        body.Should().Contain("refund-policy");
    }

    [Fact]
    public async Task UpsertPage_ReturnsOk()
    {
        var resp = await _client.PutAsync("/api/store/pages", Json(new
        {
            title = "About Us",
            handle = "about-us",
            bodyHtml = "<h1>About</h1>"
        }));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OAuthInstall_WithShop_ReturnsAuthorizeUrl()
    {
        var resp = await _client.GetAsync("/api/oauth/install?shop=example.myshopify.com");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("authorizeUrl");
    }

    [Fact]
    public async Task OAuthInstall_WithoutShop_Returns400()
    {
        var resp = await _client.GetAsync("/api/oauth/install");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OAuthCallback_PersistsToken()
    {
        var resp = await _client.GetAsync("/api/oauth/callback?shop=example.myshopify.com&code=abc123");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await _client.GetAsync("/api/oauth/stores");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        (await list.Content.ReadAsStringAsync()).Should().Contain("example.myshopify.com");
    }

    [Fact]
    public async Task Webhook_OrderCreated_ReturnsOk()
    {
        var payload = JsonSerializer.Serialize(new { id = 12345L, total = 99.99m });
        var resp = await _client.PostAsync("/api/webhook/order-created",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Webhook_OrderRefunded_ReturnsOk()
    {
        var payload = JsonSerializer.Serialize(new { id = 54321L });
        var resp = await _client.PostAsync("/api/webhook/order-refunded",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
