using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using AutoCommerce.Shared.Events;
using AutoCommerce.StoreManagement.Domain;
using AutoCommerce.StoreManagement.Infrastructure;
using AutoCommerce.StoreManagement.Services;

namespace AutoCommerce.StoreManagement.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WebhookController : ControllerBase
{
    private readonly ILogger<WebhookController> _logger;
    private readonly IBrainClient _brain;
    private readonly StoreDbContext _db;

    public WebhookController(ILogger<WebhookController> logger, IBrainClient brain, StoreDbContext db)
    {
        _logger = logger;
        _brain = brain;
        _db = db;
    }

    [HttpPost("order-created")]
    public async Task<IActionResult> OnOrderCreated(CancellationToken ct = default)
    {
        try
        {
            var body = await new StreamReader(Request.Body).ReadToEndAsync(ct);
            _logger.LogInformation("Received order.created webhook: {Payload}", body);

            var payload = ExtractOrderPayload(body);
            await _brain.PublishEventAsync(
                DomainEvent.Create(EventTypes.OrderCreated, "store-manager", payload), ct);

            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing order.created webhook");
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    [HttpPost("order-refunded")]
    public async Task<IActionResult> OnOrderRefunded(CancellationToken ct = default)
    {
        try
        {
            var body = await new StreamReader(Request.Body).ReadToEndAsync(ct);
            _logger.LogInformation("Received order.refunded webhook: {Payload}", body);

            var payload = ExtractOrderPayload(body);
            await _brain.PublishEventAsync(
                DomainEvent.Create("order.refunded", "store-manager", payload), ct);

            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing order.refunded webhook");
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    private static object ExtractOrderPayload(string body)
    {
        try
        {
            var order = JsonSerializer.Deserialize<JsonElement>(body);
            long? orderId = order.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number
                ? idProp.GetInt64() : null;
            return new { orderId, raw = body };
        }
        catch
        {
            return new { raw = body };
        }
    }
}

[ApiController]
[Route("api/oauth")]
public class OAuthController : ControllerBase
{
    private readonly StoreDbContext _db;
    private readonly ILogger<OAuthController> _logger;

    public OAuthController(StoreDbContext db, ILogger<OAuthController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet("install")]
    public IActionResult Install([FromQuery] string shop)
    {
        if (string.IsNullOrWhiteSpace(shop))
            return BadRequest(new { error = "shop parameter required" });

        var clientId = HttpContext.RequestServices.GetService<IConfiguration>()?["Shopify:ApiKey"] ?? "mock-client-id";
        var redirectUri = $"{Request.Scheme}://{Request.Host}/api/oauth/callback";
        var scopes = "read_products,write_products,read_orders,write_orders,read_themes,write_themes,read_content,write_content";
        var url = $"https://{shop}/admin/oauth/authorize?client_id={clientId}&scope={Uri.EscapeDataString(scopes)}&redirect_uri={Uri.EscapeDataString(redirectUri)}";
        return Ok(new { authorizeUrl = url });
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string shop, [FromQuery] string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(shop) || string.IsNullOrWhiteSpace(code))
            return BadRequest(new { error = "shop and code required" });

        // In real life we'd exchange `code` for a permanent access_token with Shopify.
        // Here we persist a placeholder securely so the flow is testable end-to-end.
        var token = $"mock-token-{Guid.NewGuid():N}";
        var cfg = HttpContext.RequestServices.GetService<IConfiguration>();
        var apiKey = cfg?["Shopify:ApiKey"] ?? "mock-client-id";

        var existing = await _db.Stores.FirstOrDefaultAsync(s => s.ShopName == shop, ct);
        if (existing == null)
        {
            _db.Stores.Add(new ShopifyStore
            {
                ShopName = shop,
                AccessToken = token,
                ApiKey = apiKey,
                IsInitialized = true
            });
        }
        else
        {
            existing.AccessToken = token;
            existing.IsInitialized = true;
        }
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("OAuth callback stored token for {Shop}", shop);
        return Ok(new { success = true, shop });
    }

    [HttpGet("stores")]
    public async Task<IActionResult> ListStores(CancellationToken ct = default)
    {
        var stores = await _db.Stores
            .Select(s => new { s.Id, s.ShopName, s.IsInitialized, s.CreatedAt })
            .ToListAsync(ct);
        return Ok(stores);
    }
}
