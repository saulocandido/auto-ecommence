using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;

namespace AutoCommerce.SupplierSelection.Services;

public class PriceMonitorOptions
{
    public bool Enabled { get; set; } = false;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromMinutes(1);
    public double PriceChangeProbability { get; set; } = 0.10;
    public double MaxChangePercent { get; set; } = 0.15;
    public int Seed { get; set; } = 0;
}

public class SupplierPriceMonitor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PriceMonitorOptions _options;
    private readonly Random _random;
    private readonly ILogger<SupplierPriceMonitor> _logger;

    public SupplierPriceMonitor(
        IServiceScopeFactory scopeFactory,
        PriceMonitorOptions options,
        ILogger<SupplierPriceMonitor> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _random = options.Seed == 0 ? new Random() : new Random(options.Seed);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("SupplierPriceMonitor disabled");
            return;
        }

        try { await Task.Delay(_options.StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ScanOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Supplier price scan failed"); }

            try { await Task.Delay(_options.PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task ScanOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var brain = scope.ServiceProvider.GetRequiredService<IBrainClient>();

        var products = await brain.ListProductsAsync(status: null, ct);
        foreach (var product in products)
        {
            if (string.IsNullOrEmpty(product.SupplierKey) || product.Cost is not { } oldCost || oldCost <= 0) continue;

            double roll;
            lock (_random) roll = _random.NextDouble();
            if (roll > _options.PriceChangeProbability) continue;

            double delta;
            lock (_random) delta = (_random.NextDouble() * 2 - 1) * _options.MaxChangePercent;
            var newCost = decimal.Round(oldCost * (decimal)(1 + delta), 2);
            if (newCost <= 0 || newCost == oldCost) continue;

            var payload = new SupplierPriceChangedPayload(
                product.Id, product.SupplierKey!, oldCost, newCost, "USD", DateTimeOffset.UtcNow);
            await brain.PublishEventAsync(
                DomainEvent.Create(EventTypes.SupplierPriceChanged, "supplier-selection", payload), ct);

            _logger.LogInformation("Simulated price change for {ExternalId}: {Old} → {New}",
                product.ExternalId, oldCost, newCost);
        }
    }
}
