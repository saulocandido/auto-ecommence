using Microsoft.AspNetCore.Mvc;
using AutoCommerce.StoreManagement.Services;

namespace AutoCommerce.StoreManagement.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StoreController : ControllerBase
{
    private readonly IStoreService _storeService;
    private readonly ILogger<StoreController> _logger;

    public StoreController(IStoreService storeService, ILogger<StoreController> logger)
    {
        _storeService = storeService;
        _logger = logger;
    }

    [HttpPost("initialize")]
    public async Task<IActionResult> Initialize(CancellationToken ct = default)
    {
        try
        {
            await _storeService.InitializeStoreAsync(ct);
            return Ok(new { success = true, message = "Store initialized" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing store");
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    [HttpPost("sync-product")]
    public async Task<IActionResult> SyncProduct([FromBody] SyncProductRequest request, CancellationToken ct = default)
    {
        try
        {
            await _storeService.SyncProductAsync(
                request.BrainProductId, request.Title, request.Description,
                request.Price, request.ImageUrl, request.Variants, request.StockQuantity, ct);
            return Ok(new { success = true, message = "Product synced to Shopify" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing product {ProductId}", request.BrainProductId);
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    [HttpPost("sync-price")]
    public async Task<IActionResult> UpdatePrice([FromBody] UpdatePriceRequest request, CancellationToken ct = default)
    {
        try
        {
            await _storeService.UpdateProductPriceAsync(request.BrainProductId, request.NewPrice, ct);
            return Ok(new { success = true, message = "Price updated" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating price for {ProductId}", request.BrainProductId);
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    [HttpPost("sync-status")]
    public async Task<IActionResult> UpdateStatus([FromBody] UpdateStatusRequest request, CancellationToken ct = default)
    {
        try
        {
            await _storeService.UpdateProductStatusAsync(request.BrainProductId, request.Status, ct);
            return Ok(new { success = true, message = "Status updated" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating status for {ProductId}", request.BrainProductId);
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    [HttpPost("sync-stock")]
    public async Task<IActionResult> UpdateStock([FromBody] UpdateStockRequest request, CancellationToken ct = default)
    {
        try
        {
            await _storeService.UpdateProductStockAsync(request.BrainProductId, request.Quantity, ct);
            return Ok(new { success = true, message = "Stock updated" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating stock for {ProductId}", request.BrainProductId);
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    [HttpGet("products")]
    public async Task<IActionResult> GetProducts(
        [FromServices] IBrainClient brain,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        try
        {
            var products = await brain.GetProductsAsync(status, ct);
            return Ok(products);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading products from Brain");
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    [HttpGet("theme")]
    public async Task<IActionResult> GetTheme(CancellationToken ct = default)
    {
        var cfg = await _storeService.GetThemeAsync(ct);
        return Ok(cfg);
    }

    [HttpPut("theme")]
    public async Task<IActionResult> UpdateTheme([FromBody] ShopifyThemeConfig config, CancellationToken ct = default)
    {
        try
        {
            var theme = await _storeService.UpdateThemeAsync(config, ct);
            return Ok(new { success = true, theme });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating theme");
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    [HttpGet("pages")]
    public async Task<IActionResult> ListPages(CancellationToken ct = default)
    {
        var pages = await _storeService.ListPagesAsync(ct);
        return Ok(pages);
    }

    [HttpPut("pages")]
    public async Task<IActionResult> UpsertPage([FromBody] UpsertPageRequest request, CancellationToken ct = default)
    {
        try
        {
            var page = await _storeService.UpsertPageAsync(request.Title, request.Handle, request.BodyHtml, ct);
            return Ok(new { success = true, page });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error upserting page");
            return BadRequest(new { success = false, error = ex.Message });
        }
    }
}

public record SyncProductRequest(
    Guid BrainProductId,
    string Title,
    string Description,
    decimal Price,
    string? ImageUrl,
    IReadOnlyList<ShopifyVariant>? Variants = null,
    int StockQuantity = 0);

public record UpdatePriceRequest(Guid BrainProductId, decimal NewPrice);
public record UpdateStatusRequest(Guid BrainProductId, string Status);
public record UpdateStockRequest(Guid BrainProductId, int Quantity);
public record UpsertPageRequest(string Title, string Handle, string BodyHtml);
