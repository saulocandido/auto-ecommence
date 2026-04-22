using System.Collections.Concurrent;

namespace AutoCommerce.StoreManagement.Services;

public interface IShopifyMetrics
{
    void Increment(string name);
    long Get(string name);
    IReadOnlyDictionary<string, long> Snapshot();
}

public class ShopifyMetrics : IShopifyMetrics
{
    private readonly ConcurrentDictionary<string, long> _counters = new();

    public void Increment(string name) =>
        _counters.AddOrUpdate(name, 1, (_, v) => v + 1);

    public long Get(string name) =>
        _counters.TryGetValue(name, out var v) ? v : 0;

    public IReadOnlyDictionary<string, long> Snapshot() =>
        _counters.ToDictionary(kv => kv.Key, kv => kv.Value);

    public static class Names
    {
        public const string ProductsCreated = "products.created";
        public const string ProductsUpdated = "products.updated";
        public const string ProductsArchived = "products.archived";
        public const string ProductsUnpublished = "products.unpublished";
        public const string PriceUpdates = "price.updated";
        public const string StockUpdates = "stock.updated";
        public const string SyncSuccess = "sync.success";
        public const string SyncFailure = "sync.failure";
        public const string WebhooksReceived = "webhooks.received";
        public const string WebhooksRejected = "webhooks.rejected";
        public const string EventsProcessed = "events.processed";
        public const string EventsFailed = "events.failed";
    }
}

public static class ShopifyEventTypes
{
    public const string ProductCreated = "shopify.product_created";
    public const string ProductUpdated = "shopify.product_updated";
    public const string ProductArchived = "shopify.product_archived";
    public const string PriceUpdated = "shopify.price_updated";
    public const string StockUpdated = "shopify.stock_updated";
    public const string SyncFailed = "shopify.sync_failed";
}
