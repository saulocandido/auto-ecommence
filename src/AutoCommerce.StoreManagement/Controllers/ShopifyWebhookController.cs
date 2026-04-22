using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoCommerce.Shared.Events;
using AutoCommerce.StoreManagement.Domain;
using AutoCommerce.StoreManagement.Infrastructure;
using AutoCommerce.StoreManagement.Services;

namespace AutoCommerce.StoreManagement.Controllers;

[ApiController]
[Route("api/shopify/webhooks")]
public class ShopifyWebhookController : ControllerBase
{
    private readonly StoreDbContext _db;
    private readonly IShopifySyncService _sync;
    private readonly IBrainClient _brain;
    private readonly IShopifyMetrics _metrics;
    private readonly IConfiguration _config;
    private readonly ILogger<ShopifyWebhookController> _logger;

    public ShopifyWebhookController(
        StoreDbContext db,
        IShopifySyncService sync,
        IBrainClient brain,
        IShopifyMetrics metrics,
        IConfiguration config,
        ILogger<ShopifyWebhookController> logger)
    {
        _db = db;
        _sync = sync;
        _brain = brain;
        _metrics = metrics;
        _config = config;
        _logger = logger;
    }

    private async Task<(bool Ok, string Body)> ReadAndVerifyAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        var raw = ms.ToArray();
        var body = Encoding.UTF8.GetString(raw);

        var secret = await GetWebhookSecretAsync(ct);
        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogWarning("No webhook secret configured; accepting unverified webhook (dev only)");
            return (true, body);
        }

        if (!Request.Headers.TryGetValue("X-Shopify-Hmac-Sha256", out var hmacHeader))
        {
            _metrics.Increment(ShopifyMetrics.Names.WebhooksRejected);
            return (false, body);
        }

        using var alg = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computed = Convert.ToBase64String(alg.ComputeHash(raw));
        var provided = hmacHeader.ToString();

        var ok = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(provided));

        if (!ok) _metrics.Increment(ShopifyMetrics.Names.WebhooksRejected);
        return (ok, body);
    }

    private async Task<string?> GetWebhookSecretAsync(CancellationToken ct)
    {
        var cfg = await _db.AdminConfigs.AsNoTracking().FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(cfg?.WebhookSecret)) return cfg.WebhookSecret;
        return _config["Shopify:WebhookSecret"];
    }

    [HttpPost("products/create")]
    public async Task<IActionResult> ProductsCreate(CancellationToken ct = default)
    {
        _metrics.Increment(ShopifyMetrics.Names.WebhooksReceived);
        var (ok, body) = await ReadAndVerifyAsync(ct);
        if (!ok) return Unauthorized(new { error = "invalid HMAC" });
        await HandleRemoteProductChange(body, "products/create", ct);
        return Ok();
    }

    [HttpPost("products/update")]
    public async Task<IActionResult> ProductsUpdate(CancellationToken ct = default)
    {
        _metrics.Increment(ShopifyMetrics.Names.WebhooksReceived);
        var (ok, body) = await ReadAndVerifyAsync(ct);
        if (!ok) return Unauthorized(new { error = "invalid HMAC" });
        await HandleRemoteProductChange(body, "products/update", ct);
        return Ok();
    }

    [HttpPost("orders/create")]
    public async Task<IActionResult> OrdersCreate(CancellationToken ct = default)
    {
        _metrics.Increment(ShopifyMetrics.Names.WebhooksReceived);
        var (ok, body) = await ReadAndVerifyAsync(ct);
        if (!ok) return Unauthorized(new { error = "invalid HMAC" });

        var payload = ParseOrderPayload(body);
        await _brain.PublishEventAsync(
            DomainEvent.Create(EventTypes.OrderCreated, "store-manager", payload), ct);
        return Ok();
    }

    [HttpPost("orders/updated")]
    public async Task<IActionResult> OrdersUpdated(CancellationToken ct = default)
    {
        _metrics.Increment(ShopifyMetrics.Names.WebhooksReceived);
        var (ok, body) = await ReadAndVerifyAsync(ct);
        if (!ok) return Unauthorized(new { error = "invalid HMAC" });

        var payload = ParseOrderPayload(body);
        await _brain.PublishEventAsync(
            DomainEvent.Create("order.updated", "store-manager", payload), ct);
        return Ok();
    }

    [HttpPost("app/uninstalled")]
    public async Task<IActionResult> AppUninstalled(CancellationToken ct = default)
    {
        _metrics.Increment(ShopifyMetrics.Names.WebhooksReceived);
        var (ok, body) = await ReadAndVerifyAsync(ct);
        if (!ok) return Unauthorized(new { error = "invalid HMAC" });

        _logger.LogWarning("Shopify app/uninstalled webhook received: {Body}", body);
        await _brain.PublishEventAsync(
            DomainEvent.Create("shopify.app_uninstalled", "store-manager", new { raw = body }), ct);
        return Ok();
    }

    private async Task HandleRemoteProductChange(string body, string source, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            long? shopifyId = root.TryGetProperty("id", out var idP) && idP.ValueKind == JsonValueKind.Number
                ? idP.GetInt64() : null;
            DateTimeOffset updatedAt = DateTimeOffset.UtcNow;
            if (root.TryGetProperty("updated_at", out var u) && u.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(u.GetString(), out var parsed))
                updatedAt = parsed;

            if (shopifyId.HasValue)
                await _sync.HandleRemoteProductChangeAsync(shopifyId.Value, updatedAt, ct);
            else
                _logger.LogWarning("Shopify webhook {Source} missing product id", source);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Shopify webhook {Source}", source);
        }
    }

    private static object ParseOrderPayload(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            long? orderId = root.TryGetProperty("id", out var idP) && idP.ValueKind == JsonValueKind.Number
                ? idP.GetInt64() : null;
            decimal? total = root.TryGetProperty("total_price", out var t) && t.ValueKind == JsonValueKind.String
                && decimal.TryParse(t.GetString(), out var d) ? d : null;
            return new { orderId, total, raw = body };
        }
        catch
        {
            return new { raw = body };
        }
    }
}
