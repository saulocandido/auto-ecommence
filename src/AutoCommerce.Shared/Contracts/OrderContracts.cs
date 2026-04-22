namespace AutoCommerce.Shared.Contracts;

public enum OrderStatus
{
    Pending,
    SentToSupplier,
    Fulfilled,
    Failed,
    Cancelled,
    Refunded
}

public sealed record OrderLineDto(
    Guid ProductId,
    int Quantity,
    decimal UnitPrice);

public sealed record OrderCreateDto(
    string ShopOrderId,
    string CustomerEmail,
    string? CustomerName,
    string ShippingCountry,
    IReadOnlyList<OrderLineDto> Lines);

public sealed record OrderResponse(
    Guid Id,
    string ShopOrderId,
    string? SupplierOrderId,
    string CustomerEmail,
    string? CustomerName,
    string ShippingCountry,
    string Status,
    string? TrackingNumber,
    string? TrackingUrl,
    decimal Total,
    IReadOnlyList<OrderLineDto> Lines,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record OrderTrackingUpdateDto(
    string Status,
    string? TrackingNumber,
    string? TrackingUrl,
    string? SupplierOrderId);
