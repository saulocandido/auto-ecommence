using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;
using AutoCommerce.SupplierSelection.Domain;

namespace AutoCommerce.SupplierSelection.Services;

public class FulfillmentOptions
{
    // If set, overrides supplier reliability entirely (useful for tests). 1.0 = always succeed, 0.0 = always fail.
    public double? ForcedSuccessRate { get; set; }
    public int MinDeliveryDays { get; set; } = 4;
    public int MaxDeliveryDays { get; set; } = 14;
    public int RandomSeed { get; set; } = 0;
}

public interface IFulfillmentService
{
    Task<FulfillmentResult> FulfillAsync(FulfillmentRequest request, CancellationToken ct);
}

public class FulfillmentService : IFulfillmentService
{
    private readonly IBrainClient _brain;
    private readonly ISupplierCatalog _catalog;
    private readonly FulfillmentOptions _options;
    private readonly Random _random;
    private readonly ILogger<FulfillmentService> _logger;

    public FulfillmentService(
        IBrainClient brain,
        ISupplierCatalog catalog,
        FulfillmentOptions options,
        ILogger<FulfillmentService> logger)
    {
        _brain = brain;
        _catalog = catalog;
        _options = options;
        _random = options.RandomSeed == 0 ? new Random() : new Random(options.RandomSeed);
        _logger = logger;
    }

    public async Task<FulfillmentResult> FulfillAsync(FulfillmentRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SupplierKey))
        {
            var failed = new FulfillmentResult(false, null, null, null, null, "no supplier assigned");
            await PublishFailedAsync(request, failed.Reason!, ct);
            return failed;
        }

        var profile = _catalog.Get(request.SupplierKey);
        var threshold = _options.ForcedSuccessRate ?? (profile?.BaseReliability ?? 0.5);

        lock (_random)
        {
            if (_random.NextDouble() > threshold)
            {
                var reason = "supplier declined order (simulated)";
                var failed = new FulfillmentResult(false, null, null, null, null, reason);
                _ = PublishFailedAsync(request, reason, ct);
                return failed;
            }
        }

        var supplierOrderId = $"SUP-{Guid.NewGuid():N}".Substring(0, 12).ToUpperInvariant();
        var tracking = $"TRK{DateTimeOffset.UtcNow:yyMMdd}-{_random.Next(1000, 9999)}";
        var trackingUrl = $"https://track.autocommerce.local/{tracking}";
        var deliveryDays = _random.Next(_options.MinDeliveryDays, _options.MaxDeliveryDays + 1);
        var eta = DateTimeOffset.UtcNow.AddDays(deliveryDays);

        var result = new FulfillmentResult(true, supplierOrderId, tracking, trackingUrl, eta, null);
        await PublishSentAsync(request, result, ct);
        _logger.LogInformation("Fulfilled order {ShopOrderId} via {Supplier} tracking={Tracking}",
            request.ShopOrderId, request.SupplierKey, tracking);
        return result;
    }

    private Task PublishSentAsync(FulfillmentRequest request, FulfillmentResult result, CancellationToken ct)
    {
        var payload = new OrderSentToSupplierPayload(
            request.OrderId, request.ShopOrderId, request.SupplierKey!,
            result.SupplierOrderId!, result.TrackingNumber, result.TrackingUrl, result.EstimatedDelivery);
        return _brain.PublishEventAsync(
            DomainEvent.Create(EventTypes.OrderSentToSupplier, "supplier-selection", payload), ct);
    }

    private Task PublishFailedAsync(FulfillmentRequest request, string reason, CancellationToken ct)
    {
        var payload = new
        {
            orderId = request.OrderId,
            shopOrderId = request.ShopOrderId,
            supplierKey = request.SupplierKey,
            reason
        };
        return _brain.PublishEventAsync(
            DomainEvent.Create(EventTypes.OrderFulfillmentFailed, "supplier-selection", payload), ct);
    }
}
