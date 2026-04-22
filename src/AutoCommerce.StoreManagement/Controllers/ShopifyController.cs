using Microsoft.AspNetCore.Mvc;
using AutoCommerce.StoreManagement.Services;

namespace AutoCommerce.StoreManagement.Controllers;

[ApiController]
[Route("api/shopify")]
public class ShopifyController : ControllerBase
{
    private readonly IShopifySyncService _sync;
    private readonly IShopifyMetrics _metrics;
    private readonly ILogger<ShopifyController> _logger;

    public ShopifyController(IShopifySyncService sync, IShopifyMetrics metrics, ILogger<ShopifyController> logger)
    {
        _sync = sync;
        _metrics = metrics;
        _logger = logger;
    }

    [HttpPost("sync-product")]
    public async Task<IActionResult> SyncProduct([FromBody] BrainProductRequest req, CancellationToken ct = default)
    {
        var result = await _sync.SyncProductAsync(req.BrainProductId, ct);
        return result.Error == null ? Ok(result) : BadRequest(result);
    }

    [HttpPost("sync-products/bulk")]
    public async Task<IActionResult> BulkSync([FromBody] BulkSyncRequest? req, CancellationToken ct = default)
    {
        var results = await _sync.BulkSyncAsync(req?.BrainProductIds, ct);
        return Ok(new { count = results.Count, results });
    }

    [HttpPost("sync-price")]
    public async Task<IActionResult> SyncPrice([FromBody] SyncPriceRequest req, CancellationToken ct = default)
    {
        var result = await _sync.SyncPriceAsync(req.BrainProductId, req.NewPrice, ct);
        return result.Error == null ? Ok(result) : BadRequest(result);
    }

    [HttpPost("sync-stock")]
    public async Task<IActionResult> SyncStock([FromBody] SyncStockRequest req, CancellationToken ct = default)
    {
        var result = await _sync.SyncStockAsync(req.BrainProductId, req.Quantity, ct);
        return result.Error == null ? Ok(result) : BadRequest(result);
    }

    [HttpPost("archive-product")]
    public async Task<IActionResult> ArchiveProduct([FromBody] ArchiveRequest req, CancellationToken ct = default)
    {
        var result = await _sync.ArchiveProductAsync(req.BrainProductId, req.Reason ?? "manual", ct);
        return result.Error == null ? Ok(result) : BadRequest(result);
    }

    [HttpPost("publish-product")]
    public async Task<IActionResult> PublishProduct([FromBody] BrainProductRequest req, CancellationToken ct = default)
    {
        var result = await _sync.PublishProductAsync(req.BrainProductId, ct);
        return result.Error == null ? Ok(result) : BadRequest(result);
    }

    [HttpGet("sync-status/{productId:guid}")]
    public async Task<IActionResult> GetSyncStatus(Guid productId, CancellationToken ct = default)
    {
        var row = await _sync.GetSyncStatusAsync(productId, ct);
        if (row == null) return NotFound(new { productId, status = "not_synced" });
        return Ok(row);
    }

    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken ct = default)
    {
        var h = await _sync.HealthCheckAsync(ct);
        return Ok(h);
    }

    [HttpGet("metrics")]
    public IActionResult Metrics() => Ok(_metrics.Snapshot());
}

public record BrainProductRequest(Guid BrainProductId);
public record BulkSyncRequest(List<Guid>? BrainProductIds);
public record SyncPriceRequest(Guid BrainProductId, decimal NewPrice);
public record SyncStockRequest(Guid BrainProductId, int Quantity);
public record ArchiveRequest(Guid BrainProductId, string? Reason);
