using System.Text.Json;
using AutoCommerce.Shared.Events;

namespace AutoCommerce.SupplierSelection.Services;

public class DiscoveredWorkerOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(10);
}

public class ProductDiscoveredWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DiscoveredWorkerOptions _options;
    private readonly ILogger<ProductDiscoveredWorker> _logger;
    private DateTimeOffset _lastSeen = DateTimeOffset.UtcNow.AddHours(-1);

    public ProductDiscoveredWorker(
        IServiceScopeFactory scopeFactory,
        DiscoveredWorkerOptions options,
        ILogger<ProductDiscoveredWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("ProductDiscoveredWorker disabled");
            return;
        }

        try { await Task.Delay(_options.StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation("ProductDiscoveredWorker polling every {Interval}", _options.PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Discovery poll failed, will retry");
            }

            try { await Task.Delay(_options.PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var brain = scope.ServiceProvider.GetRequiredService<IBrainClient>();
        var selection = scope.ServiceProvider.GetRequiredService<ISelectionService>();

        var events = await brain.PollEventsAsync(EventTypes.ProductDiscovered, _lastSeen, 100, ct);
        if (events.Count == 0) return;

        foreach (var evt in events)
        {
            try
            {
                var productId = ExtractProductId(evt.PayloadJson);
                if (productId is null) continue;
                await selection.SelectAndAssignAsync(productId.Value, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process discovered event {Id}", evt.Id);
            }
            if (evt.OccurredAt > _lastSeen) _lastSeen = evt.OccurredAt;
        }
    }

    internal static Guid? ExtractProductId(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        using var doc = JsonDocument.Parse(payloadJson);
        if (doc.RootElement.TryGetProperty("id", out var id) && id.TryGetGuid(out var g)) return g;
        if (doc.RootElement.TryGetProperty("productId", out var pid) && pid.TryGetGuid(out var g2)) return g2;
        if (doc.RootElement.TryGetProperty("Id", out var id2) && id2.TryGetGuid(out var g3)) return g3;
        return null;
    }
}
