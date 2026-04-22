namespace AutoCommerce.StoreManagement.Domain;

public class ShopifyStore
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ShopName { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public bool IsInitialized { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum ShopifySyncStatus
{
    Pending,
    Synced,
    Failed,
    Archived,
    Unpublished
}

public class ShopifyProductSync
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BrainProductId { get; set; }
    public long ShopifyProductId { get; set; }

    public string Title { get; set; } = string.Empty;
    public decimal Price { get; set; }

    // Expanded tracking per spec
    public string VariantIdsJson { get; set; } = "[]";
    public string? SupplierKey { get; set; }
    public string? SourceSupplierUrl { get; set; }
    public string SyncStatus { get; set; } = ShopifySyncStatus.Pending.ToString();
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSyncAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LastSyncError { get; set; }
    public bool ManagedBySystem { get; set; } = true;
    public string PublicationStatus { get; set; } = "active";
    public DateTimeOffset? PriceSourceAt { get; set; }
    public DateTimeOffset? StockSourceAt { get; set; }
    public int LastKnownStock { get; set; }
}

public class ShopifyAdminConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ShopDomain { get; set; } = string.Empty;
    public string? AccessToken { get; set; }
    public string? WebhookSecret { get; set; }
    public string DefaultPublicationStatus { get; set; } = "active";
    public string ArchiveBehaviour { get; set; } = "archive";
    public bool AutoArchiveOnZeroStock { get; set; } = true;
    public string ManagedTag { get; set; } = "autocommerce-managed";
    public string MetafieldNamespace { get; set; } = "autocommerce";
    public int MaxRetryAttempts { get; set; } = 5;
    public int RetryBaseDelayMs { get; set; } = 500;
    public string ConflictStrategy { get; set; } = "overwrite";
    public string SalesChannelsJson { get; set; } = "[\"online_store\"]";
    public string CollectionMappingJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class EventCheckpoint
{
    public string EventType { get; set; } = string.Empty;
    public DateTimeOffset LastProcessedAt { get; set; } = DateTimeOffset.UtcNow.AddHours(-1);
}

public class DeadLetterItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Operation { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? NextRetryAt { get; set; }
}
