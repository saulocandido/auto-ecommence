using AutoCommerce.Brain.Domain;
using AutoCommerce.Brain.Infrastructure;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;
using Microsoft.EntityFrameworkCore;

namespace AutoCommerce.Brain.Services;

public interface IOrderService
{
    Task<OrderResponse> CreateAsync(OrderCreateDto dto, CancellationToken ct);
    Task<OrderResponse?> GetAsync(Guid id, CancellationToken ct);
    Task<OrderResponse?> GetByShopOrderIdAsync(string shopOrderId, CancellationToken ct);
    Task<IReadOnlyList<OrderResponse>> ListAsync(string? status, int skip, int take, CancellationToken ct);
    Task<OrderResponse?> UpdateTrackingAsync(Guid id, OrderTrackingUpdateDto dto, CancellationToken ct);
}

public class OrderService : IOrderService
{
    private readonly BrainDbContext _db;
    private readonly IEventBus _bus;
    private readonly ILogger<OrderService> _logger;

    public OrderService(BrainDbContext db, IEventBus bus, ILogger<OrderService> logger)
    {
        _db = db;
        _bus = bus;
        _logger = logger;
    }

    public async Task<OrderResponse> CreateAsync(OrderCreateDto dto, CancellationToken ct)
    {
        var existing = await _db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.ShopOrderId == dto.ShopOrderId, ct);
        if (existing is not null) return ToResponse(existing);

        var order = new Order
        {
            ShopOrderId = dto.ShopOrderId,
            CustomerEmail = dto.CustomerEmail,
            CustomerName = dto.CustomerName,
            ShippingCountry = dto.ShippingCountry,
            Status = OrderStatus.Pending,
            Total = dto.Lines.Sum(l => l.UnitPrice * l.Quantity),
            Lines = dto.Lines.Select(l => new OrderLine
            {
                ProductId = l.ProductId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice
            }).ToList()
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct);

        await _bus.PublishAsync(DomainEvent.Create(EventTypes.OrderCreated, "brain",
            new { order.Id, order.ShopOrderId, order.Total, Lines = order.Lines.Select(l => new { l.ProductId, l.Quantity, l.UnitPrice }) }), ct);
        _logger.LogInformation("Order {ShopOrderId} created, total {Total}", order.ShopOrderId, order.Total);
        return ToResponse(order);
    }

    public async Task<OrderResponse?> GetAsync(Guid id, CancellationToken ct)
    {
        var o = await _db.Orders.Include(x => x.Lines).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return o is null ? null : ToResponse(o);
    }

    public async Task<OrderResponse?> GetByShopOrderIdAsync(string shopOrderId, CancellationToken ct)
    {
        var o = await _db.Orders.Include(x => x.Lines).AsNoTracking().FirstOrDefaultAsync(x => x.ShopOrderId == shopOrderId, ct);
        return o is null ? null : ToResponse(o);
    }

    public async Task<IReadOnlyList<OrderResponse>> ListAsync(string? status, int skip, int take, CancellationToken ct)
    {
        var q = _db.Orders.Include(x => x.Lines).AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<OrderStatus>(status, true, out var s))
            q = q.Where(o => o.Status == s);
        var list = await q.OrderByDescending(o => o.CreatedAt).Skip(skip).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        return list.Select(ToResponse).ToList();
    }

    public async Task<OrderResponse?> UpdateTrackingAsync(Guid id, OrderTrackingUpdateDto dto, CancellationToken ct)
    {
        var o = await _db.Orders.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return null;

        if (dto.TrackingNumber is not null) o.TrackingNumber = dto.TrackingNumber;
        if (dto.TrackingUrl is not null) o.TrackingUrl = dto.TrackingUrl;
        if (dto.SupplierOrderId is not null) o.SupplierOrderId = dto.SupplierOrderId;

        if (Enum.TryParse<OrderStatus>(dto.Status, true, out var s))
        {
            o.Status = s;
            o.UpdatedAt = DateTimeOffset.UtcNow;
            var type = s switch
            {
                OrderStatus.SentToSupplier => EventTypes.OrderSentToSupplier,
                OrderStatus.Fulfilled => EventTypes.OrderFulfilled,
                OrderStatus.Failed => EventTypes.OrderFulfillmentFailed,
                _ => null
            };
            if (type is not null)
                await _bus.PublishAsync(DomainEvent.Create(type, "brain",
                    new { o.Id, o.ShopOrderId, o.SupplierOrderId, o.TrackingNumber, o.TrackingUrl, o.Status }), ct);
        }

        await _db.SaveChangesAsync(ct);
        return ToResponse(o);
    }

    private static OrderResponse ToResponse(Order o) => new(
        o.Id, o.ShopOrderId, o.SupplierOrderId, o.CustomerEmail, o.CustomerName, o.ShippingCountry,
        o.Status.ToString(), o.TrackingNumber, o.TrackingUrl, o.Total,
        o.Lines.Select(l => new OrderLineDto(l.ProductId, l.Quantity, l.UnitPrice)).ToList(),
        o.CreatedAt, o.UpdatedAt);
}
