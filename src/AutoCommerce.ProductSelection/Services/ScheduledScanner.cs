namespace AutoCommerce.ProductSelection.Services;

public class ScheduledScannerOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(10);
}

public class ScheduledScanner : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ScheduledScannerOptions _opts;
    private readonly ILogger<ScheduledScanner> _logger;

    public ScheduledScanner(IServiceScopeFactory scopes, ScheduledScannerOptions opts, ILogger<ScheduledScanner> logger)
    {
        _scopes = scopes;
        _opts = opts;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled)
        {
            _logger.LogInformation("Scheduled scanner disabled");
            return;
        }

        try { await Task.Delay(_opts.StartupDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var orch = scope.ServiceProvider.GetRequiredService<ISelectionOrchestrator>();
                var count = await orch.DiscoverAndImportAsync(null, stoppingToken);
                _logger.LogInformation("Scheduled scan imported {Count} products", count);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled scan failed");
            }

            try { await Task.Delay(_opts.Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }
}
