using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;
using AutoCommerce.StoreManagement.Domain;
using AutoCommerce.StoreManagement.Infrastructure;

namespace AutoCommerce.StoreManagement.Services;

public class BrainEventSubscriber : BackgroundService
{
    private static readonly string[] SubscribedTypes =
    {
        EventTypes.ProductApproved,      // product.approved
        EventTypes.ProductPaused,        // product.paused
        EventTypes.ProductKilled,        // product.kill
        EventTypes.SupplierSelected,     // supplier.selected
        EventTypes.SupplierPriceChanged, // supplier.price_changed
        EventTypes.SupplierStockChanged, // supplier.stock_changed
        EventTypes.PriceUpdated,         // price.updated
        EventTypes.ProductLinkCorrected, // product.link_corrected
        EventTypes.ProductLinkInvalid    // product.link_invalid
    };

    private readonly IServiceProvider _services;
    private readonly ILogger<BrainEventSubscriber> _logger;
    private readonly TimeSpan _pollInterval;

    public BrainEventSubscriber(IServiceProvider services, ILogger<BrainEventSubscriber> logger, IConfiguration config)
    {
        _services = services;
        _logger = logger;
        var seconds = int.TryParse(config["Shopify:PollIntervalSeconds"], out var s) ? s : 10;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(2, seconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BrainEventSubscriber started; polling every {Interval}s for {Count} event types",
            _pollInterval.TotalSeconds, SubscribedTypes.Length);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BrainEventSubscriber poll cycle failed");
            }
            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var brain = sp.GetRequiredService<IBrainClient>();
        var db = sp.GetRequiredService<StoreDbContext>();
        var sync = sp.GetRequiredService<IShopifySyncService>();
        var metrics = sp.GetRequiredService<IShopifyMetrics>();

        foreach (var type in SubscribedTypes)
        {
            ct.ThrowIfCancellationRequested();
            var cp = await db.EventCheckpoints.FirstOrDefaultAsync(x => x.EventType == type, ct);
            if (cp == null)
            {
                cp = new EventCheckpoint { EventType = type, LastProcessedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
                db.EventCheckpoints.Add(cp);
                await db.SaveChangesAsync(ct);
            }

            var events = await brain.PollEventsAsync(type, cp.LastProcessedAt, 100, ct);
            if (events.Count == 0) continue;

            var ordered = events.OrderBy(e => e.OccurredAt).ToList();
            foreach (var evt in ordered)
            {
                try
                {
                    await DispatchAsync(sync, evt, ct);
                    metrics.Increment(ShopifyMetrics.Names.EventsProcessed);
                    cp.LastProcessedAt = evt.OccurredAt;
                }
                catch (Exception ex)
                {
                    metrics.Increment(ShopifyMetrics.Names.EventsFailed);
                    _logger.LogError(ex, "Failed to dispatch event {Id} type={Type}", evt.Id, evt.Type);
                    db.DeadLetters.Add(new DeadLetterItem
                    {
                        Operation = $"subscriber.{evt.Type}",
                        PayloadJson = evt.PayloadJson,
                        Error = ex.ToString().Length > 4000 ? ex.ToString()[..4000] : ex.ToString(),
                        AttemptCount = 1,
                        NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(5)
                    });
                    // advance checkpoint anyway to avoid infinite loop; DLQ captures the issue
                    cp.LastProcessedAt = evt.OccurredAt;
                }
            }
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task DispatchAsync(IShopifySyncService sync, RecentEventWithPayload evt, CancellationToken ct)
    {
        var brainProductId = ExtractBrainProductId(evt.PayloadJson);

        switch (evt.Type)
        {
            case EventTypes.ProductApproved:
            case EventTypes.SupplierSelected:
            case EventTypes.ProductLinkCorrected:
                if (brainProductId.HasValue) await sync.SyncProductAsync(brainProductId.Value, ct);
                break;

            case EventTypes.ProductPaused:
                if (brainProductId.HasValue) await sync.ArchiveProductAsync(brainProductId.Value, "product.paused", ct);
                break;

            case EventTypes.ProductKilled:
            case EventTypes.ProductLinkInvalid:
                if (brainProductId.HasValue) await sync.ArchiveProductAsync(brainProductId.Value, evt.Type, ct);
                break;

            case EventTypes.SupplierPriceChanged:
            case EventTypes.PriceUpdated:
                if (brainProductId.HasValue)
                {
                    var price = ExtractDecimal(evt.PayloadJson, "newPrice", "price", "cost");
                    if (price.HasValue) await sync.SyncPriceAsync(brainProductId.Value, price.Value, ct);
                    else await sync.SyncProductAsync(brainProductId.Value, ct);
                }
                break;

            case EventTypes.SupplierStockChanged:
                if (brainProductId.HasValue)
                {
                    var qty = ExtractInt(evt.PayloadJson, "stock", "stockAvailable", "quantity");
                    if (qty.HasValue) await sync.SyncStockAsync(brainProductId.Value, qty.Value, ct);
                    else await sync.SyncProductAsync(brainProductId.Value, ct);
                }
                break;
        }
    }

    private static Guid? ExtractBrainProductId(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            foreach (var prop in new[] { "brainProductId", "productId", "id", "BrainProductId", "ProductId", "Id" })
            {
                if (doc.RootElement.TryGetProperty(prop, out var v))
                {
                    if (v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g)) return g;
                }
            }
        }
        catch { }
        return null;
    }

    private static decimal? ExtractDecimal(string payloadJson, params string[] keys)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            foreach (var k in keys)
                if (doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number)
                    return v.GetDecimal();
        }
        catch { }
        return null;
    }

    private static int? ExtractInt(string payloadJson, params string[] keys)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            foreach (var k in keys)
                if (doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number)
                    return v.GetInt32();
        }
        catch { }
        return null;
    }
}
