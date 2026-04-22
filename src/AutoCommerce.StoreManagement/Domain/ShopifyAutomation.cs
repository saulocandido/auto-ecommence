namespace AutoCommerce.StoreManagement.Domain;

public enum AutomationRunStatus { Pending, Running, Completed, Failed, Stopped, LoginRequired }
public enum AutomationProductStatus { Ready, Processing, Imported, Pushed, Failed, ManualReview }
public enum AutomationStep { Idle, NavigatingToYl, Searching, Matching, Importing, NavigatingImportList, Pushing, Done }

public class ShopifyAutomationConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ShopifyStoreUrl { get; set; } = string.Empty;
    public string FindProductsUrl { get; set; } = string.Empty;
    public string ImportListUrl { get; set; } = string.Empty;
    public string AppUrl { get; set; } = string.Empty;
    public string ShopifyApiKey { get; set; } = string.Empty;
    public string ShopifyHost { get; set; } = string.Empty;
    public string DefaultSearch { get; set; } = string.Empty;
    public string AuthMode { get; set; } = "session"; // session | token | cookie
    public int MaxRetries { get; set; } = 3;
    public double MatchConfidenceThreshold { get; set; } = 0.6;
    public bool HeadlessMode { get; set; } = true;
    public bool UseApiFirst { get; set; } = true;
    public string? SessionCookie { get; set; }
    public string? AuthToken { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ShopifyAutomationRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Status { get; set; } = AutomationRunStatus.Pending.ToString();
    public int TotalProducts { get; set; }
    public int ProcessedCount { get; set; }
    public int ImportedCount { get; set; }
    public int PushedCount { get; set; }
    public int FailedCount { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public class ShopifyAutomationProduct
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public Guid BrainProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? SupplierKey { get; set; }
    public string Status { get; set; } = AutomationProductStatus.Ready.ToString();
    public string CurrentStep { get; set; } = AutomationStep.Idle.ToString();
    public string? MatchedResultTitle { get; set; }
    public string? MatchedExternalId { get; set; }
    public string? ImportItemId { get; set; }
    public double Confidence { get; set; }
    public string? ErrorReason { get; set; }
    public long? ShopifyProductId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ShopifyAutomationLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public Guid? ProductId { get; set; }
    public string Level { get; set; } = "info"; // info, warn, error
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
