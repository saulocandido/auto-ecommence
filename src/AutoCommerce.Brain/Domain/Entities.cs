using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AutoCommerce.Shared.Contracts;

namespace AutoCommerce.Brain.Domain;

public class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(200)] public string ExternalId { get; set; } = string.Empty;
    [Required, MaxLength(500)] public string Title { get; set; } = string.Empty;
    [Required, MaxLength(120)] public string Category { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ImageUrlsJson { get; set; } = "[]";
    public string TagsJson { get; set; } = "[]";
    [MaxLength(8)] public string TargetMarket { get; set; } = "EU";
    public double Score { get; set; }
    public decimal? Cost { get; set; }
    public decimal? Price { get; set; }
    public ProductStatus Status { get; set; } = ProductStatus.Draft;
    [MaxLength(64)] public string? SupplierKey { get; set; }
    public string SuppliersJson { get; set; } = "[]";
    public string? ScoreBreakdownJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [NotMapped]
    public double? MarginPercent => (Price is > 0 && Cost is > 0)
        ? (double)((Price - Cost) / Price) * 100.0
        : null;
}

public class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(64)] public string ShopOrderId { get; set; } = string.Empty;
    [MaxLength(64)] public string? SupplierOrderId { get; set; }
    [Required, MaxLength(320)] public string CustomerEmail { get; set; } = string.Empty;
    [MaxLength(200)] public string? CustomerName { get; set; }
    [Required, MaxLength(2)] public string ShippingCountry { get; set; } = "IE";
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    [MaxLength(128)] public string? TrackingNumber { get; set; }
    [MaxLength(500)] public string? TrackingUrl { get; set; }
    public decimal Total { get; set; }
    public List<OrderLine> Lines { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class OrderLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Order? Order { get; set; }
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public class PricingRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(120)] public string Category { get; set; } = string.Empty;
    public double MarkupMultiplier { get; set; } = 2.5;
    public double MinMarginPercent { get; set; } = 25.0;
    public decimal MinPrice { get; set; } = 5m;
    public decimal MaxPrice { get; set; } = 200m;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class EventLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(80)] public string Type { get; set; } = string.Empty;
    [Required, MaxLength(80)] public string Source { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(64)] public string Name { get; set; } = string.Empty;
    [Required, MaxLength(128)] public string KeyHash { get; set; } = string.Empty;
    [MaxLength(200)] public string Scopes { get; set; } = "*";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Revoked { get; set; }
}
