using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoCommerce.StoreManagement.Domain;
using AutoCommerce.StoreManagement.Infrastructure;

namespace AutoCommerce.StoreManagement.Controllers;

[ApiController]
[Route("api/shopify/admin")]
public class AdminConfigController : ControllerBase
{
    private readonly StoreDbContext _db;
    private readonly ILogger<AdminConfigController> _logger;

    public AdminConfigController(StoreDbContext db, ILogger<AdminConfigController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig(CancellationToken ct = default)
    {
        var cfg = await _db.AdminConfigs.FirstOrDefaultAsync(ct);
        if (cfg == null)
        {
            cfg = new ShopifyAdminConfig();
            _db.AdminConfigs.Add(cfg);
            await _db.SaveChangesAsync(ct);
        }
        return Ok(Sanitize(cfg));
    }

    [HttpPut("config")]
    public async Task<IActionResult> UpdateConfig([FromBody] AdminConfigUpdateDto dto, CancellationToken ct = default)
    {
        var cfg = await _db.AdminConfigs.FirstOrDefaultAsync(ct);
        if (cfg == null)
        {
            cfg = new ShopifyAdminConfig();
            _db.AdminConfigs.Add(cfg);
        }

        if (dto.ShopDomain != null) cfg.ShopDomain = dto.ShopDomain;
        if (dto.AccessToken != null) cfg.AccessToken = dto.AccessToken;
        if (dto.WebhookSecret != null) cfg.WebhookSecret = dto.WebhookSecret;
        if (dto.DefaultPublicationStatus != null) cfg.DefaultPublicationStatus = dto.DefaultPublicationStatus;
        if (dto.ArchiveBehaviour != null) cfg.ArchiveBehaviour = dto.ArchiveBehaviour;
        if (dto.AutoArchiveOnZeroStock.HasValue) cfg.AutoArchiveOnZeroStock = dto.AutoArchiveOnZeroStock.Value;
        if (dto.ManagedTag != null) cfg.ManagedTag = dto.ManagedTag;
        if (dto.MetafieldNamespace != null) cfg.MetafieldNamespace = dto.MetafieldNamespace;
        if (dto.MaxRetryAttempts.HasValue) cfg.MaxRetryAttempts = dto.MaxRetryAttempts.Value;
        if (dto.RetryBaseDelayMs.HasValue) cfg.RetryBaseDelayMs = dto.RetryBaseDelayMs.Value;
        if (dto.ConflictStrategy != null) cfg.ConflictStrategy = dto.ConflictStrategy;
        if (dto.SalesChannelsJson != null) cfg.SalesChannelsJson = dto.SalesChannelsJson;
        if (dto.CollectionMappingJson != null) cfg.CollectionMappingJson = dto.CollectionMappingJson;
        cfg.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Shopify admin config updated");
        return Ok(Sanitize(cfg));
    }

    [HttpGet("sync-status")]
    public async Task<IActionResult> ListSyncStatus([FromQuery] string? status = null, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        var q = _db.ProductSyncs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.SyncStatus == status);
        var rows = await q.Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        return Ok(rows);
    }

    [HttpGet("dead-letters")]
    public async Task<IActionResult> ListDeadLetters([FromQuery] int take = 100, CancellationToken ct = default)
    {
        var rows = await _db.DeadLetters.AsNoTracking()
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);
        return Ok(rows);
    }

    private static object Sanitize(ShopifyAdminConfig cfg) => new
    {
        cfg.Id,
        cfg.ShopDomain,
        HasAccessToken = !string.IsNullOrEmpty(cfg.AccessToken),
        HasWebhookSecret = !string.IsNullOrEmpty(cfg.WebhookSecret),
        cfg.DefaultPublicationStatus,
        cfg.ArchiveBehaviour,
        cfg.AutoArchiveOnZeroStock,
        cfg.ManagedTag,
        cfg.MetafieldNamespace,
        cfg.MaxRetryAttempts,
        cfg.RetryBaseDelayMs,
        cfg.ConflictStrategy,
        cfg.SalesChannelsJson,
        cfg.CollectionMappingJson,
        cfg.UpdatedAt
    };
}

public record AdminConfigUpdateDto(
    string? ShopDomain,
    string? AccessToken,
    string? WebhookSecret,
    string? DefaultPublicationStatus,
    string? ArchiveBehaviour,
    bool? AutoArchiveOnZeroStock,
    string? ManagedTag,
    string? MetafieldNamespace,
    int? MaxRetryAttempts,
    int? RetryBaseDelayMs,
    string? ConflictStrategy,
    string? SalesChannelsJson,
    string? CollectionMappingJson);
