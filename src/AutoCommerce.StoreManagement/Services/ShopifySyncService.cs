using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;
using AutoCommerce.StoreManagement.Domain;
using AutoCommerce.StoreManagement.Infrastructure;

namespace AutoCommerce.StoreManagement.Services;

public record ShopifySyncResult(
    Guid BrainProductId,
    long? ShopifyProductId,
    string Status,
    string? Error = null);

public record ShopifyHealthResult(
    bool Connected,
    int ManagedProductCount,
    int PendingCount,
    int FailedCount,
    IReadOnlyDictionary<string, long> Metrics);

public interface IShopifySyncService
{
    Task<ShopifySyncResult> SyncProductAsync(Guid brainProductId, CancellationToken ct = default);
    Task<IReadOnlyList<ShopifySyncResult>> BulkSyncAsync(IEnumerable<Guid>? brainProductIds, CancellationToken ct = default);
    Task<ShopifySyncResult> SyncPriceAsync(Guid brainProductId, decimal newPrice, CancellationToken ct = default);
    Task<ShopifySyncResult> SyncStockAsync(Guid brainProductId, int quantity, CancellationToken ct = default);
    Task<ShopifySyncResult> ArchiveProductAsync(Guid brainProductId, string reason, CancellationToken ct = default);
    Task<ShopifySyncResult> PublishProductAsync(Guid brainProductId, CancellationToken ct = default);
    Task<ShopifyProductSync?> GetSyncStatusAsync(Guid brainProductId, CancellationToken ct = default);
    Task<ShopifyHealthResult> HealthCheckAsync(CancellationToken ct = default);
    Task HandleRemoteProductChangeAsync(long shopifyProductId, DateTimeOffset remoteUpdatedAt, CancellationToken ct = default);
}

public class ShopifySyncService : IShopifySyncService
{
    private readonly IShopifyClient _shopify;
    private readonly IBrainClient _brain;
    private readonly StoreDbContext _db;
    private readonly IRetryPolicy _retry;
    private readonly IShopifyMetrics _metrics;
    private readonly ILogger<ShopifySyncService> _logger;

    private const string BrainProductIdKey = "brain_product_id";
    private const string SupplierKeyKey = "supplier_key";
    private const string SyncMetadataKey = "sync_metadata";

    public ShopifySyncService(
        IShopifyClient shopify,
        IBrainClient brain,
        StoreDbContext db,
        IRetryPolicy retry,
        IShopifyMetrics metrics,
        ILogger<ShopifySyncService> logger)
    {
        _shopify = shopify;
        _brain = brain;
        _db = db;
        _retry = retry;
        _metrics = metrics;
        _logger = logger;
    }

    private async Task<ShopifyAdminConfig> GetConfigAsync(CancellationToken ct)
    {
        var cfg = await _db.AdminConfigs.FirstOrDefaultAsync(ct);
        if (cfg != null) return cfg;
        cfg = new ShopifyAdminConfig();
        _db.AdminConfigs.Add(cfg);
        await _db.SaveChangesAsync(ct);
        return cfg;
    }

    public async Task<ShopifySyncResult> SyncProductAsync(Guid brainProductId, CancellationToken ct = default)
    {
        var product = await _brain.GetProductAsync(brainProductId, ct);
        if (product == null)
            return await FailAsync(brainProductId, null, "sync.product", $"Brain product {brainProductId} not found", ct);

        var cfg = await GetConfigAsync(ct);
        var row = await _db.ProductSyncs.FirstOrDefaultAsync(x => x.BrainProductId == brainProductId, ct);

        if (row != null && !row.ManagedBySystem)
        {
            _logger.LogInformation("Skipping {BrainId}: product is not managed by system", brainProductId);
            return new ShopifySyncResult(brainProductId, row.ShopifyProductId, "skipped_unmanaged");
        }

        try
        {
            // If status is killed/paused, archive instead of publishing
            if (string.Equals(product.Status, "Killed", StringComparison.OrdinalIgnoreCase))
                return await InternalArchiveAsync(brainProductId, product, row, cfg, "brain.product_killed", ct);

            var price = product.Price ?? 0m;
            var stock = product.Suppliers?.FirstOrDefault()?.StockAvailable ?? 0;
            var supplierKey = product.SupplierKey ?? product.Suppliers?.FirstOrDefault()?.SupplierKey;
            var supplierUrl = product.Suppliers?.FirstOrDefault()?.Url;

            // Idempotency: prefer stored ShopifyProductId; otherwise look up by metafield
            long? shopifyId = row?.ShopifyProductId;
            if (!shopifyId.HasValue || shopifyId.Value == 0)
            {
                shopifyId = await _retry.ExecuteAsync(
                    t => _shopify.FindProductByMetafieldAsync(cfg.MetafieldNamespace, BrainProductIdKey, brainProductId.ToString(), t),
                    "shopify.find_by_metafield", ct);
            }

            var tags = BuildTags(product.Tags, cfg.ManagedTag);
            var input = new ShopifyProductInput(
                Title: product.Title,
                Description: product.Description ?? string.Empty,
                Price: price,
                ImageUrl: product.ImageUrls?.FirstOrDefault(),
                Metadata: null,
                Variants: null,
                StockQuantity: stock,
                Status: MapStatus(product.Status, cfg.DefaultPublicationStatus),
                Handle: null,
                Vendor: supplierKey,
                ProductType: product.Category,
                Tags: tags,
                ImageUrls: product.ImageUrls?.ToList(),
                SeoTitle: product.Title,
                SeoDescription: product.Description,
                ImageAltText: product.Title,
                CompareAtPrice: product.Cost);

            ShopifyProductOutput output;
            bool created = false;
            if (shopifyId.HasValue && shopifyId.Value > 0)
            {
                output = await _retry.ExecuteAsync(t => _shopify.UpdateProductAsync(shopifyId.Value, input, t), "shopify.update_product", ct);
                _metrics.Increment(ShopifyMetrics.Names.ProductsUpdated);
            }
            else
            {
                output = await _retry.ExecuteAsync(t => _shopify.CreateProductAsync(input, t), "shopify.create_product", ct);
                created = true;
                _metrics.Increment(ShopifyMetrics.Names.ProductsCreated);
            }

            // Tag + metafields + images + channels
            await _retry.ExecuteAsync(t => _shopify.AddTagsAsync(output.Id, tags, t), "shopify.add_tags", ct);
            await _retry.ExecuteAsync(t => _shopify.SetMetafieldAsync(output.Id,
                new ShopifyMetafield(cfg.MetafieldNamespace, BrainProductIdKey, brainProductId.ToString()), t),
                "shopify.set_metafield.brain_id", ct);
            if (!string.IsNullOrWhiteSpace(supplierKey))
                await _retry.ExecuteAsync(t => _shopify.SetMetafieldAsync(output.Id,
                    new ShopifyMetafield(cfg.MetafieldNamespace, SupplierKeyKey, supplierKey!), t),
                    "shopify.set_metafield.supplier", ct);
            await _retry.ExecuteAsync(t => _shopify.SetMetafieldAsync(output.Id,
                new ShopifyMetafield(cfg.MetafieldNamespace, SyncMetadataKey,
                    JsonSerializer.Serialize(new { syncedAt = DateTimeOffset.UtcNow, source = "autocommerce" }),
                    "json"), t), "shopify.set_metafield.sync_meta", ct);

            if (product.ImageUrls is { Count: > 0 })
                await _retry.ExecuteAsync(t => _shopify.UploadImagesAsync(output.Id, product.ImageUrls.ToList(), product.Title, t),
                    "shopify.upload_images", ct);

            var channels = ParseChannels(cfg.SalesChannelsJson);
            if (channels.Count > 0)
                await _retry.ExecuteAsync(t => _shopify.PublishToChannelsAsync(output.Id, channels, t),
                    "shopify.publish_channels", ct);

            await _retry.ExecuteAsync(t => _shopify.UpdateInventoryAsync(output.Id, stock, t), "shopify.update_inventory", ct);

            // Auto-archive on zero stock
            string publicationStatus = input.Status;
            if (stock <= 0 && cfg.AutoArchiveOnZeroStock)
            {
                if (string.Equals(cfg.ArchiveBehaviour, "archive", StringComparison.OrdinalIgnoreCase))
                {
                    await _retry.ExecuteAsync(t => _shopify.SetProductStatusAsync(output.Id, "archived", t), "shopify.archive", ct);
                    publicationStatus = "archived";
                    _metrics.Increment(ShopifyMetrics.Names.ProductsArchived);
                }
                else
                {
                    await _retry.ExecuteAsync(t => _shopify.SetProductStatusAsync(output.Id, "draft", t), "shopify.unpublish", ct);
                    publicationStatus = "draft";
                    _metrics.Increment(ShopifyMetrics.Names.ProductsUnpublished);
                }
            }
            else if (created && !string.Equals(publicationStatus, "archived", StringComparison.OrdinalIgnoreCase))
            {
                await _retry.ExecuteAsync(t => _shopify.PublishProductAsync(output.Id, t), "shopify.publish", ct);
            }

            await PersistRowAsync(brainProductId, output, input, supplierKey, supplierUrl, stock, publicationStatus, ShopifySyncStatus.Synced, null, ct);

            var outputEvent = created ? ShopifyEventTypes.ProductCreated : ShopifyEventTypes.ProductUpdated;
            await _brain.PublishEventAsync(DomainEvent.Create(outputEvent, "store-manager",
                new { brainProductId, shopifyProductId = output.Id, status = publicationStatus }), ct);

            _metrics.Increment(ShopifyMetrics.Names.SyncSuccess);
            return new ShopifySyncResult(brainProductId, output.Id, publicationStatus);
        }
        catch (Exception ex)
        {
            return await FailAsync(brainProductId, row?.ShopifyProductId, "sync.product", ex.ToString(), ct);
        }
    }

    public async Task<IReadOnlyList<ShopifySyncResult>> BulkSyncAsync(IEnumerable<Guid>? brainProductIds, CancellationToken ct = default)
    {
        List<Guid> ids;
        if (brainProductIds != null)
        {
            ids = brainProductIds.Distinct().ToList();
        }
        else
        {
            var products = await _brain.GetProductsAsync(null, ct);
            ids = products.Where(p => !string.Equals(p.Status, "Killed", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Id).ToList();
        }

        var results = new List<ShopifySyncResult>(ids.Count);
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await SyncProductAsync(id, ct));
        }
        return results;
    }

    public async Task<ShopifySyncResult> SyncPriceAsync(Guid brainProductId, decimal newPrice, CancellationToken ct = default)
    {
        var row = await _db.ProductSyncs.FirstOrDefaultAsync(x => x.BrainProductId == brainProductId, ct);
        if (row == null)
            return await FailAsync(brainProductId, null, "sync.price", "Product not mapped to Shopify", ct);
        if (!row.ManagedBySystem)
            return new ShopifySyncResult(brainProductId, row.ShopifyProductId, "skipped_unmanaged");

        try
        {
            var existing = await _retry.ExecuteAsync(t => _shopify.GetProductAsync(row.ShopifyProductId, t), "shopify.get_product", ct);
            if (existing == null)
                return await FailAsync(brainProductId, row.ShopifyProductId, "sync.price", "Shopify product missing", ct);

            var input = new ShopifyProductInput(existing.Title, "", newPrice, null, null,
                Status: existing.Status, StockQuantity: existing.StockQuantity);
            await _retry.ExecuteAsync(t => _shopify.UpdateProductAsync(row.ShopifyProductId, input, t), "shopify.update_price", ct);

            row.Price = newPrice;
            row.PriceSourceAt = DateTimeOffset.UtcNow;
            row.LastSyncAt = DateTimeOffset.UtcNow;
            row.SyncedAt = DateTimeOffset.UtcNow;
            row.LastSyncError = null;
            row.SyncStatus = ShopifySyncStatus.Synced.ToString();
            await _db.SaveChangesAsync(ct);

            _metrics.Increment(ShopifyMetrics.Names.PriceUpdates);
            _metrics.Increment(ShopifyMetrics.Names.SyncSuccess);

            await _brain.PublishEventAsync(DomainEvent.Create(ShopifyEventTypes.PriceUpdated, "store-manager",
                new { brainProductId, shopifyProductId = row.ShopifyProductId, newPrice }), ct);

            return new ShopifySyncResult(brainProductId, row.ShopifyProductId, "price_synced");
        }
        catch (Exception ex)
        {
            return await FailAsync(brainProductId, row.ShopifyProductId, "sync.price", ex.ToString(), ct);
        }
    }

    public async Task<ShopifySyncResult> SyncStockAsync(Guid brainProductId, int quantity, CancellationToken ct = default)
    {
        var row = await _db.ProductSyncs.FirstOrDefaultAsync(x => x.BrainProductId == brainProductId, ct);
        if (row == null)
            return await FailAsync(brainProductId, null, "sync.stock", "Product not mapped to Shopify", ct);
        if (!row.ManagedBySystem)
            return new ShopifySyncResult(brainProductId, row.ShopifyProductId, "skipped_unmanaged");

        try
        {
            var cfg = await GetConfigAsync(ct);
            await _retry.ExecuteAsync(t => _shopify.UpdateInventoryAsync(row.ShopifyProductId, quantity, t), "shopify.update_stock", ct);

            var publicationStatus = row.PublicationStatus;
            if (quantity <= 0 && cfg.AutoArchiveOnZeroStock)
            {
                var target = string.Equals(cfg.ArchiveBehaviour, "archive", StringComparison.OrdinalIgnoreCase) ? "archived" : "draft";
                await _retry.ExecuteAsync(t => _shopify.SetProductStatusAsync(row.ShopifyProductId, target, t), "shopify.auto_archive", ct);
                publicationStatus = target;
                if (target == "archived") _metrics.Increment(ShopifyMetrics.Names.ProductsArchived);
                else _metrics.Increment(ShopifyMetrics.Names.ProductsUnpublished);
            }
            else if (quantity > 0 && (row.PublicationStatus == "archived" || row.PublicationStatus == "draft"))
            {
                await _retry.ExecuteAsync(t => _shopify.SetProductStatusAsync(row.ShopifyProductId, cfg.DefaultPublicationStatus, t), "shopify.republish", ct);
                publicationStatus = cfg.DefaultPublicationStatus;
            }

            row.LastKnownStock = quantity;
            row.StockSourceAt = DateTimeOffset.UtcNow;
            row.LastSyncAt = DateTimeOffset.UtcNow;
            row.SyncedAt = DateTimeOffset.UtcNow;
            row.PublicationStatus = publicationStatus;
            row.LastSyncError = null;
            row.SyncStatus = ShopifySyncStatus.Synced.ToString();
            await _db.SaveChangesAsync(ct);

            _metrics.Increment(ShopifyMetrics.Names.StockUpdates);
            _metrics.Increment(ShopifyMetrics.Names.SyncSuccess);

            await _brain.PublishEventAsync(DomainEvent.Create(ShopifyEventTypes.StockUpdated, "store-manager",
                new { brainProductId, shopifyProductId = row.ShopifyProductId, quantity, publicationStatus }), ct);

            return new ShopifySyncResult(brainProductId, row.ShopifyProductId, "stock_synced");
        }
        catch (Exception ex)
        {
            return await FailAsync(brainProductId, row.ShopifyProductId, "sync.stock", ex.ToString(), ct);
        }
    }

    public async Task<ShopifySyncResult> ArchiveProductAsync(Guid brainProductId, string reason, CancellationToken ct = default)
    {
        var product = await _brain.GetProductAsync(brainProductId, ct);
        var row = await _db.ProductSyncs.FirstOrDefaultAsync(x => x.BrainProductId == brainProductId, ct);
        var cfg = await GetConfigAsync(ct);
        return await InternalArchiveAsync(brainProductId, product, row, cfg, reason, ct);
    }

    private async Task<ShopifySyncResult> InternalArchiveAsync(Guid brainProductId, ProductResponse? product,
        ShopifyProductSync? row, ShopifyAdminConfig cfg, string reason, CancellationToken ct)
    {
        if (row == null || row.ShopifyProductId == 0)
            return new ShopifySyncResult(brainProductId, null, "not_mapped");
        if (!row.ManagedBySystem)
            return new ShopifySyncResult(brainProductId, row.ShopifyProductId, "skipped_unmanaged");

        try
        {
            var target = string.Equals(cfg.ArchiveBehaviour, "archive", StringComparison.OrdinalIgnoreCase) ? "archived" : "draft";
            await _retry.ExecuteAsync(t => _shopify.SetProductStatusAsync(row.ShopifyProductId, target, t), "shopify.archive", ct);

            row.PublicationStatus = target;
            row.SyncStatus = (target == "archived" ? ShopifySyncStatus.Archived : ShopifySyncStatus.Unpublished).ToString();
            row.LastSyncAt = DateTimeOffset.UtcNow;
            row.SyncedAt = DateTimeOffset.UtcNow;
            row.LastSyncError = null;
            await _db.SaveChangesAsync(ct);

            if (target == "archived") _metrics.Increment(ShopifyMetrics.Names.ProductsArchived);
            else _metrics.Increment(ShopifyMetrics.Names.ProductsUnpublished);
            _metrics.Increment(ShopifyMetrics.Names.SyncSuccess);

            await _brain.PublishEventAsync(DomainEvent.Create(ShopifyEventTypes.ProductArchived, "store-manager",
                new { brainProductId, shopifyProductId = row.ShopifyProductId, status = target, reason }), ct);

            return new ShopifySyncResult(brainProductId, row.ShopifyProductId, target);
        }
        catch (Exception ex)
        {
            return await FailAsync(brainProductId, row.ShopifyProductId, "archive", ex.ToString(), ct);
        }
    }

    public async Task<ShopifySyncResult> PublishProductAsync(Guid brainProductId, CancellationToken ct = default)
    {
        var row = await _db.ProductSyncs.FirstOrDefaultAsync(x => x.BrainProductId == brainProductId, ct);
        if (row == null)
            return await FailAsync(brainProductId, null, "publish", "Product not mapped", ct);
        if (!row.ManagedBySystem)
            return new ShopifySyncResult(brainProductId, row.ShopifyProductId, "skipped_unmanaged");

        try
        {
            var cfg = await GetConfigAsync(ct);
            await _retry.ExecuteAsync(t => _shopify.SetProductStatusAsync(row.ShopifyProductId, cfg.DefaultPublicationStatus, t), "shopify.set_status", ct);
            await _retry.ExecuteAsync(t => _shopify.PublishProductAsync(row.ShopifyProductId, t), "shopify.publish", ct);

            row.PublicationStatus = cfg.DefaultPublicationStatus;
            row.SyncStatus = ShopifySyncStatus.Synced.ToString();
            row.LastSyncAt = DateTimeOffset.UtcNow;
            row.SyncedAt = DateTimeOffset.UtcNow;
            row.LastSyncError = null;
            await _db.SaveChangesAsync(ct);

            _metrics.Increment(ShopifyMetrics.Names.SyncSuccess);
            await _brain.PublishEventAsync(DomainEvent.Create(ShopifyEventTypes.ProductUpdated, "store-manager",
                new { brainProductId, shopifyProductId = row.ShopifyProductId, status = cfg.DefaultPublicationStatus }), ct);

            return new ShopifySyncResult(brainProductId, row.ShopifyProductId, "published");
        }
        catch (Exception ex)
        {
            return await FailAsync(brainProductId, row.ShopifyProductId, "publish", ex.ToString(), ct);
        }
    }

    public Task<ShopifyProductSync?> GetSyncStatusAsync(Guid brainProductId, CancellationToken ct = default)
        => _db.ProductSyncs.AsNoTracking().FirstOrDefaultAsync(x => x.BrainProductId == brainProductId, ct);

    public async Task<ShopifyHealthResult> HealthCheckAsync(CancellationToken ct = default)
    {
        var connected = false;
        try { connected = await _shopify.TestConnectionAsync(ct); } catch { connected = false; }

        var managedCount = await _db.ProductSyncs.CountAsync(x => x.ManagedBySystem, ct);
        var pending = await _db.ProductSyncs.CountAsync(x => x.SyncStatus == ShopifySyncStatus.Pending.ToString(), ct);
        var failed = await _db.ProductSyncs.CountAsync(x => x.SyncStatus == ShopifySyncStatus.Failed.ToString(), ct);

        return new ShopifyHealthResult(connected, managedCount, pending, failed, _metrics.Snapshot());
    }

    public async Task HandleRemoteProductChangeAsync(long shopifyProductId, DateTimeOffset remoteUpdatedAt, CancellationToken ct = default)
    {
        // Loop prevention: skip if our last sync is newer than or equal to the remote update
        var row = await _db.ProductSyncs.FirstOrDefaultAsync(x => x.ShopifyProductId == shopifyProductId, ct);
        if (row == null) return;
        if (!row.ManagedBySystem) return;
        if (row.LastSyncAt >= remoteUpdatedAt.AddSeconds(-1))
        {
            _logger.LogDebug("Ignoring remote change for {ShopifyId}: echo of our own sync", shopifyProductId);
            return;
        }
        // Trigger reconciliation by re-syncing from Brain (source of truth)
        await SyncProductAsync(row.BrainProductId, ct);
    }

    // ---------- helpers ----------

    private async Task PersistRowAsync(Guid brainProductId, ShopifyProductOutput output, ShopifyProductInput input,
        string? supplierKey, string? supplierUrl, int stock, string publicationStatus,
        ShopifySyncStatus status, string? error, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await _db.ProductSyncs.FirstOrDefaultAsync(x => x.BrainProductId == brainProductId, ct);
        var variantIdsJson = output.VariantIds != null ? JsonSerializer.Serialize(output.VariantIds) : "[]";

        if (existing == null)
        {
            _db.ProductSyncs.Add(new ShopifyProductSync
            {
                BrainProductId = brainProductId,
                ShopifyProductId = output.Id,
                Title = output.Title,
                Price = output.Price,
                VariantIdsJson = variantIdsJson,
                SupplierKey = supplierKey,
                SourceSupplierUrl = supplierUrl,
                SyncStatus = status.ToString(),
                SyncedAt = now,
                LastSyncAt = now,
                LastSyncError = error,
                ManagedBySystem = true,
                PublicationStatus = publicationStatus,
                PriceSourceAt = now,
                StockSourceAt = now,
                LastKnownStock = stock
            });
        }
        else
        {
            existing.ShopifyProductId = output.Id;
            existing.Title = output.Title;
            existing.Price = output.Price;
            existing.VariantIdsJson = variantIdsJson;
            existing.SupplierKey = supplierKey ?? existing.SupplierKey;
            existing.SourceSupplierUrl = supplierUrl ?? existing.SourceSupplierUrl;
            existing.SyncStatus = status.ToString();
            existing.SyncedAt = now;
            existing.LastSyncAt = now;
            existing.LastSyncError = error;
            existing.PublicationStatus = publicationStatus;
            existing.PriceSourceAt = now;
            existing.StockSourceAt = now;
            existing.LastKnownStock = stock;
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task<ShopifySyncResult> FailAsync(Guid brainProductId, long? shopifyId, string op, string error, CancellationToken ct)
    {
        _logger.LogError("Sync failure op={Op} brain={BrainId} shopify={ShopifyId} err={Err}", op, brainProductId, shopifyId, error);
        _metrics.Increment(ShopifyMetrics.Names.SyncFailure);

        var row = await _db.ProductSyncs.FirstOrDefaultAsync(x => x.BrainProductId == brainProductId, ct);
        if (row != null)
        {
            row.SyncStatus = ShopifySyncStatus.Failed.ToString();
            row.LastSyncError = error.Length > 2000 ? error[..2000] : error;
            row.LastSyncAt = DateTimeOffset.UtcNow;
        }

        _db.DeadLetters.Add(new DeadLetterItem
        {
            Operation = op,
            PayloadJson = JsonSerializer.Serialize(new { brainProductId, shopifyId }),
            Error = error.Length > 4000 ? error[..4000] : error,
            AttemptCount = 1,
            NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(5)
        });
        await _db.SaveChangesAsync(ct);

        try
        {
            await _brain.PublishEventAsync(DomainEvent.Create(ShopifyEventTypes.SyncFailed, "store-manager",
                new { brainProductId, shopifyProductId = shopifyId, operation = op, error }), ct);
        }
        catch { /* swallow — DLQ already recorded */ }

        return new ShopifySyncResult(brainProductId, shopifyId, "failed", error);
    }

    private static List<string> BuildTags(IReadOnlyList<string>? incoming, string managedTag)
    {
        var tags = new List<string>();
        if (incoming != null)
            foreach (var t in incoming)
                if (!string.IsNullOrWhiteSpace(t) && !tags.Contains(t)) tags.Add(t);
        if (!tags.Contains(managedTag)) tags.Add(managedTag);
        return tags;
    }

    private static string MapStatus(string brainStatus, string fallback)
    {
        return brainStatus?.ToLowerInvariant() switch
        {
            "active" => "active",
            "paused" => "draft",
            "killed" => "archived",
            "draft" => "draft",
            _ => fallback
        };
    }

    private static List<string> ParseChannels(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string> { "online_store" };
        }
    }
}
