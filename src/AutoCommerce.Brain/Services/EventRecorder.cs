using System.Text.Json;
using AutoCommerce.Brain.Domain;
using AutoCommerce.Brain.Infrastructure;
using AutoCommerce.Shared.Events;

namespace AutoCommerce.Brain.Services;

public class EventRecorder : IHostedService, IDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IEventBus _bus;
    private readonly IPricingEngine _pricing;
    private readonly ILogger<EventRecorder> _logger;
    private IDisposable? _subAll;
    private IDisposable? _subSupplierPrice;

    public EventRecorder(IServiceScopeFactory scopes, IEventBus bus, IPricingEngine pricing, ILogger<EventRecorder> logger)
    {
        _scopes = scopes;
        _bus = bus;
        _pricing = pricing;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _subAll = _bus.SubscribeAll(async (evt, token) =>
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BrainDbContext>();
            db.EventLogs.Add(new EventLog
            {
                Type = evt.Type,
                Source = evt.Source,
                OccurredAt = evt.OccurredAt,
                PayloadJson = evt.Payload.GetRawText()
            });
            await db.SaveChangesAsync(token);
        });

        _subSupplierPrice = _bus.Subscribe(EventTypes.SupplierPriceChanged, async (evt, token) =>
        {
            try
            {
                var root = evt.Payload;
                if (root.TryGetProperty("productId", out var idProp) &&
                    root.TryGetProperty("newCost", out var costProp) &&
                    idProp.TryGetGuid(out var productId) &&
                    costProp.TryGetDecimal(out var newCost))
                {
                    using var scope = _scopes.CreateScope();
                    var pricing = scope.ServiceProvider.GetRequiredService<IPricingEngine>();
                    await pricing.HandleSupplierPriceChangeAsync(productId, newCost, token);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Malformed supplier.price_changed payload");
            }
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _subAll?.Dispose();
        _subSupplierPrice?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subAll?.Dispose();
        _subSupplierPrice?.Dispose();
    }
}
