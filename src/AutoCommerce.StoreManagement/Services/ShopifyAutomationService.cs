using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.StoreManagement.Domain;
using AutoCommerce.StoreManagement.Infrastructure;

namespace AutoCommerce.StoreManagement.Services;

// ── DTOs ──

public record AutomationConfigDto(
    string ShopifyStoreUrl,
    string FindProductsUrl,
    string ImportListUrl,
    string AppUrl,
    string ShopifyApiKey,
    string ShopifyHost,
    string DefaultSearch,
    string AuthMode,
    int MaxRetries,
    double MatchConfidenceThreshold,
    bool HeadlessMode,
    bool UseApiFirst,
    string? SessionCookie,
    string? AuthToken);

public record AutomationConfigUpdateDto(
    string? ShopifyStoreUrl,
    string? FindProductsUrl,
    string? ImportListUrl,
    string? AppUrl,
    string? ShopifyApiKey,
    string? ShopifyHost,
    string? DefaultSearch,
    string? AuthMode,
    int? MaxRetries,
    double? MatchConfidenceThreshold,
    bool? HeadlessMode,
    bool? UseApiFirst,
    string? SessionCookie,
    string? AuthToken);

public record AutomationRunDto(
    Guid Id,
    string Status,
    int TotalProducts,
    int ProcessedCount,
    int ImportedCount,
    int PushedCount,
    int FailedCount,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error);

public record AutomationProductDto(
    Guid Id,
    Guid RunId,
    Guid BrainProductId,
    string ProductName,
    string? SupplierKey,
    string Status,
    string CurrentStep,
    string? MatchedResultTitle,
    double Confidence,
    string? ErrorReason,
    long? ShopifyProductId,
    DateTimeOffset UpdatedAt);

public record AutomationLogDto(
    Guid Id,
    Guid RunId,
    Guid? ProductId,
    string Level,
    string Message,
    string? Details,
    DateTimeOffset Timestamp);

public record AutomationMetricsDto(
    int Total,
    int Processing,
    int Imported,
    int Pushed,
    int Failed,
    int ManualReview);

// ── Service interface ──

public interface IShopifyAutomationService
{
    Task<AutomationConfigDto> GetConfigAsync(CancellationToken ct);
    Task<AutomationConfigDto> UpdateConfigAsync(AutomationConfigUpdateDto update, CancellationToken ct);
    Task<AutomationRunDto> StartRunAsync(CancellationToken ct);
    Task<AutomationRunDto> StopRunAsync(Guid runId, CancellationToken ct);
    Task<AutomationRunDto> RetryFailedAsync(Guid runId, CancellationToken ct);
    Task<AutomationRunDto> ResumeRunAsync(Guid runId, CancellationToken ct);
    Task<AutomationRunDto?> GetRunAsync(Guid runId, CancellationToken ct);
    Task<AutomationRunDto?> GetActiveRunAsync(CancellationToken ct);
    Task<List<AutomationRunDto>> GetRunsAsync(int take, CancellationToken ct);
    Task<List<AutomationProductDto>> GetRunProductsAsync(Guid runId, CancellationToken ct);
    Task<List<AutomationLogDto>> GetRunLogsAsync(Guid runId, int take, CancellationToken ct);
    Task<AutomationMetricsDto> GetMetricsAsync(Guid runId, CancellationToken ct);
}

// ── Implementation ──

public class ShopifyAutomationService : IShopifyAutomationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBrainClient _brain;
    private readonly IProductMatchingEngine _matcher;
    private readonly IShopifyAdminAppClient _adminApp;
    private readonly IShopifySessionManager? _sessions;
    private readonly ILogger<ShopifyAutomationService> _logger;
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runCts = new();

    public ShopifyAutomationService(
        IServiceScopeFactory scopeFactory,
        IBrainClient brain,
        IProductMatchingEngine matcher,
        IShopifyAdminAppClient adminApp,
        ILogger<ShopifyAutomationService> logger,
        IShopifySessionManager? sessions = null)
    {
        _scopeFactory = scopeFactory;
        _brain = brain;
        _matcher = matcher;
        _adminApp = adminApp;
        _logger = logger;
        _sessions = sessions;
    }

    // ── Config ──

    public async Task<AutomationConfigDto> GetConfigAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var cfg = await db.AutomationConfigs.FirstOrDefaultAsync(ct);
        if (cfg == null)
        {
            cfg = new ShopifyAutomationConfig
            {
                ShopifyStoreUrl = "https://admin.shopify.com/store/jiydad-cj",
                AppUrl = "https://app.dropshiping.ai",
                ShopifyApiKey = "36a86a25ff0c6d4958653adb9ba54e11",
                ShopifyHost = "YWRtaW4uc2hvcGlmeS5jb20vc3RvcmUvaml5ZGFkLWNq",
                DefaultSearch = "Dog Grooming Glove",
            };
            db.AutomationConfigs.Add(cfg);
            await db.SaveChangesAsync(ct);
        }
        return MapConfig(cfg);
    }

    public async Task<AutomationConfigDto> UpdateConfigAsync(AutomationConfigUpdateDto update, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var cfg = await db.AutomationConfigs.FirstOrDefaultAsync(ct);
        if (cfg == null)
        {
            cfg = new ShopifyAutomationConfig();
            db.AutomationConfigs.Add(cfg);
        }

        if (update.ShopifyStoreUrl != null) cfg.ShopifyStoreUrl = update.ShopifyStoreUrl;
        if (update.FindProductsUrl != null) cfg.FindProductsUrl = update.FindProductsUrl;
        if (update.ImportListUrl != null) cfg.ImportListUrl = update.ImportListUrl;
        if (update.AppUrl != null) cfg.AppUrl = update.AppUrl;
        if (update.ShopifyApiKey != null) cfg.ShopifyApiKey = update.ShopifyApiKey;
        if (update.ShopifyHost != null) cfg.ShopifyHost = update.ShopifyHost;
        if (update.DefaultSearch != null) cfg.DefaultSearch = update.DefaultSearch;
        if (update.AuthMode != null) cfg.AuthMode = update.AuthMode;
        if (update.MaxRetries.HasValue) cfg.MaxRetries = update.MaxRetries.Value;
        if (update.MatchConfidenceThreshold.HasValue) cfg.MatchConfidenceThreshold = update.MatchConfidenceThreshold.Value;
        if (update.HeadlessMode.HasValue) cfg.HeadlessMode = update.HeadlessMode.Value;
        if (update.UseApiFirst.HasValue) cfg.UseApiFirst = update.UseApiFirst.Value;
        if (update.SessionCookie != null) cfg.SessionCookie = update.SessionCookie;
        if (update.AuthToken != null) cfg.AuthToken = update.AuthToken;
        cfg.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return MapConfig(cfg);
    }

    // ── Run orchestration ──

    public async Task<AutomationRunDto> StartRunAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();

        var active = await db.AutomationRuns
            .FirstOrDefaultAsync(r => r.Status == AutomationRunStatus.Running.ToString(), ct);
        if (active != null)
            throw new InvalidOperationException("An automation run is already in progress");

        var cfg = await db.AutomationConfigs.FirstOrDefaultAsync(ct);
        if (cfg == null || string.IsNullOrWhiteSpace(cfg.FindProductsUrl))
            throw new InvalidOperationException("Find Products URL is not configured. Set it in automation config first.");
        if (string.IsNullOrWhiteSpace(cfg.ImportListUrl))
            throw new InvalidOperationException("Import List URL is not configured. Set it in automation config first.");

        // Auth gate — do NOT start the browser work if we already know the session is dead.
        // Skipping this when _sessions is null keeps the unit-test path simple.
        if (_sessions != null)
        {
            var status = await _sessions.ValidateAsync(cfg, ct);
            if (status.State != "connected")
                throw new InvalidOperationException(
                    $"Shopify session is not connected ({status.State}). " +
                    "Open the Shopify Session panel and connect before starting a run.");
        }

        var products = await _brain.GetProductsAsync("active", ct);
        if (products.Count == 0)
            throw new InvalidOperationException("No active products found in Brain to automate");

        var run = new ShopifyAutomationRun
        {
            Status = AutomationRunStatus.Running.ToString(),
            TotalProducts = products.Count
        };
        db.AutomationRuns.Add(run);

        var automationProducts = products.Select(p => new ShopifyAutomationProduct
        {
            RunId = run.Id,
            BrainProductId = p.Id,
            ProductName = p.Title,
            SupplierKey = p.SupplierKey,
            Status = AutomationProductStatus.Ready.ToString()
        }).ToList();
        db.AutomationProducts.AddRange(automationProducts);

        db.AutomationLogs.Add(new ShopifyAutomationLog
        {
            RunId = run.Id,
            Level = "info",
            Message = $"Automation started with {products.Count} products. Find: {cfg.FindProductsUrl} | Import: {cfg.ImportListUrl}"
        });

        await db.SaveChangesAsync(ct);

        var runId = run.Id;
        var dto = MapRun(run);

        // Snapshot config into a plain object — the current scope (and cfg's tracker) is about
        // to be disposed, the background task creates its own scope.
        var cfgSnapshot = Clone(cfg);
        var cts = new CancellationTokenSource();
        _runCts[runId] = cts;
        _ = Task.Run(() => ExecuteRunAsync(runId, cfgSnapshot, cts.Token));

        return dto;
    }

    public async Task<AutomationRunDto> StopRunAsync(Guid runId, CancellationToken ct)
    {
        if (_runCts.TryRemove(runId, out var cts))
            cts.Cancel();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var run = await db.AutomationRuns.FindAsync(new object[] { runId }, ct);
        if (run == null) throw new KeyNotFoundException("Run not found");

        run.Status = AutomationRunStatus.Stopped.ToString();
        run.CompletedAt = DateTimeOffset.UtcNow;

        db.AutomationLogs.Add(new ShopifyAutomationLog
        {
            RunId = runId,
            Level = "warn",
            Message = "Automation stopped by user"
        });

        await db.SaveChangesAsync(ct);
        return MapRun(run);
    }

    public async Task<AutomationRunDto> ResumeRunAsync(Guid runId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var run = await db.AutomationRuns.FindAsync(new object[] { runId }, ct);
        if (run == null) throw new KeyNotFoundException("Run not found");
        if (run.Status != AutomationRunStatus.LoginRequired.ToString())
            throw new InvalidOperationException($"Run is not paused for login (status={run.Status})");

        var cfg = await db.AutomationConfigs.FirstOrDefaultAsync(ct);
        if (cfg == null) throw new InvalidOperationException("No automation config");

        if (_sessions != null)
        {
            var status = await _sessions.ValidateAsync(cfg, ct);
            if (status.State != "connected")
                throw new InvalidOperationException(
                    $"Session is still not connected ({status.State}). Log in first, then resume.");
        }

        // Rewind any products that were mid-flight when the session died.
        var interrupted = await db.AutomationProducts
            .Where(p => p.RunId == runId && p.Status == AutomationProductStatus.Processing.ToString())
            .ToListAsync(ct);
        foreach (var p in interrupted)
        {
            p.Status = AutomationProductStatus.Ready.ToString();
            p.CurrentStep = AutomationStep.Idle.ToString();
            p.ErrorReason = null;
        }

        run.Status = AutomationRunStatus.Running.ToString();
        run.Error = null;
        run.CompletedAt = null;

        db.AutomationLogs.Add(new ShopifyAutomationLog
        {
            RunId = runId,
            Level = "info",
            Message = $"Run resumed after login ({interrupted.Count} products rewound)"
        });
        await db.SaveChangesAsync(ct);

        var dto = MapRun(run);
        var cfgSnapshot = Clone(cfg);
        var cts = new CancellationTokenSource();
        _runCts[runId] = cts;
        _ = Task.Run(() => ExecuteRunAsync(runId, cfgSnapshot, cts.Token));

        return dto;
    }

    public async Task<AutomationRunDto> RetryFailedAsync(Guid runId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var run = await db.AutomationRuns.FindAsync(new object[] { runId }, ct);
        if (run == null) throw new KeyNotFoundException("Run not found");

        var cfg = await db.AutomationConfigs.FirstOrDefaultAsync(ct);
        if (cfg == null) throw new InvalidOperationException("No automation config");

        var failedProducts = await db.AutomationProducts
            .Where(p => p.RunId == runId && p.Status == AutomationProductStatus.Failed.ToString())
            .ToListAsync(ct);

        foreach (var p in failedProducts)
        {
            p.Status = AutomationProductStatus.Ready.ToString();
            p.CurrentStep = AutomationStep.Idle.ToString();
            p.ErrorReason = null;
        }

        run.Status = AutomationRunStatus.Running.ToString();
        run.CompletedAt = null;
        run.FailedCount = Math.Max(0, run.FailedCount - failedProducts.Count);

        db.AutomationLogs.Add(new ShopifyAutomationLog
        {
            RunId = runId,
            Level = "info",
            Message = $"Retrying {failedProducts.Count} failed products"
        });

        await db.SaveChangesAsync(ct);

        var dto = MapRun(run);
        var cfgSnapshot = Clone(cfg);

        var cts = new CancellationTokenSource();
        _runCts[runId] = cts;
        _ = Task.Run(() => ExecuteRunAsync(runId, cfgSnapshot, cts.Token));

        return dto;
    }

    // ── Query methods ──

    public async Task<AutomationRunDto?> GetRunAsync(Guid runId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var run = await db.AutomationRuns.FindAsync(new object[] { runId }, ct);
        return run == null ? null : MapRun(run);
    }

    public async Task<AutomationRunDto?> GetActiveRunAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var run = await db.AutomationRuns
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(r => r.Status == AutomationRunStatus.Running.ToString(), ct);
        return run == null ? null : MapRun(run);
    }

    public async Task<List<AutomationRunDto>> GetRunsAsync(int take, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        // Materialise first — EF Core cannot translate static helper calls inside the projection.
        var runs = await db.AutomationRuns
            .OrderByDescending(r => r.StartedAt)
            .Take(take)
            .ToListAsync(ct);
        return runs.Select(MapRun).ToList();
    }

    public async Task<List<AutomationProductDto>> GetRunProductsAsync(Guid runId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        // Materialise first — EF Core cannot translate static helper calls inside the projection.
        var products = await db.AutomationProducts
            .Where(p => p.RunId == runId)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);
        return products.Select(MapProduct).ToList();
    }

    public async Task<List<AutomationLogDto>> GetRunLogsAsync(Guid runId, int take, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        return await db.AutomationLogs
            .Where(l => l.RunId == runId)
            .OrderByDescending(l => l.Timestamp)
            .Take(take)
            .Select(l => new AutomationLogDto(l.Id, l.RunId, l.ProductId, l.Level, l.Message, l.Details, l.Timestamp))
            .ToListAsync(ct);
    }

    public async Task<AutomationMetricsDto> GetMetricsAsync(Guid runId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var products = await db.AutomationProducts.Where(p => p.RunId == runId).ToListAsync(ct);
        return new AutomationMetricsDto(
            Total: products.Count,
            Processing: products.Count(p => p.Status == AutomationProductStatus.Processing.ToString()),
            Imported: products.Count(p => p.Status == AutomationProductStatus.Imported.ToString()),
            Pushed: products.Count(p => p.Status == AutomationProductStatus.Pushed.ToString()),
            Failed: products.Count(p => p.Status == AutomationProductStatus.Failed.ToString()),
            ManualReview: products.Count(p => p.Status == AutomationProductStatus.ManualReview.ToString()));
    }

    // ── Core automation loop ──
    // Exposed as internal so integration tests can run the loop inline.
    internal async Task ExecuteRunAsync(Guid runId, ShopifyAutomationConfig cfg, CancellationToken ct)
    {
        try
        {
            await LogAsync(runId, null, "info", $"Step 1: Find Products → {cfg.FindProductsUrl}");

            // Baseline import-list count — we'll verify it increased after Phase A.
            int baselineImportCount = 0;
            try
            {
                var baseline = await _adminApp.GetImportListAsync(cfg, ct);
                baselineImportCount = baseline.Count;
                await LogAsync(runId, null, "info", $"Import list baseline: {baselineImportCount} item(s)");
            }
            catch (Exception ex)
            {
                await LogAsync(runId, null, "warn", $"Could not read baseline import list count: {ex.Message}");
            }

            // ── Phase A: find + import each product ──
            List<ShopifyAutomationProduct> products;
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
                products = await db.AutomationProducts
                    .Where(p => p.RunId == runId && p.Status == AutomationProductStatus.Ready.ToString())
                    .ToListAsync(ct);
            }

            foreach (var product in products)
            {
                if (ct.IsCancellationRequested) break;
                await SearchAndImportProductAsync(runId, product, cfg, ct);
            }

            // Verify the import-list actually grew.
            int afterAddsCount = baselineImportCount;
            try
            {
                var afterAdds = await _adminApp.GetImportListAsync(cfg, ct);
                afterAddsCount = afterAdds.Count;
                var delta = afterAddsCount - baselineImportCount;
                if (delta > 0)
                    await LogAsync(runId, null, "info",
                        $"Import list grew: {baselineImportCount} → {afterAddsCount} (+{delta})");
                else
                    await LogAsync(runId, null, "warn",
                        $"Import list count did NOT increase (was {baselineImportCount}, now {afterAddsCount}) — upstream add step may have silently failed");
            }
            catch (Exception ex)
            {
                await LogAsync(runId, null, "warn", $"Could not verify import list count after adds: {ex.Message}");
            }

            // ── Phase B: push every imported item to the store ──
            await PushImportedProductsAsync(runId, cfg, ct);

            // ── Finalise run ──
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
                var run = await db.AutomationRuns.FindAsync(new object[] { runId }, ct);
                if (run != null && run.Status == AutomationRunStatus.Running.ToString())
                {
                    var allProducts = await db.AutomationProducts.Where(p => p.RunId == runId).ToListAsync(ct);
                    run.ProcessedCount = allProducts.Count(p => p.Status != AutomationProductStatus.Ready.ToString());
                    run.ImportedCount = allProducts.Count(p => p.Status == AutomationProductStatus.Imported.ToString());
                    run.PushedCount = allProducts.Count(p => p.Status == AutomationProductStatus.Pushed.ToString());
                    run.FailedCount = allProducts.Count(p => p.Status == AutomationProductStatus.Failed.ToString());
                    run.Status = AutomationRunStatus.Completed.ToString();
                    run.CompletedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
            }

            await LogAsync(runId, null, "info", "Automation run completed");
        }
        catch (OperationCanceledException)
        {
            await LogAsync(runId, null, "warn", "Automation run cancelled");
        }
        catch (SessionExpiredException sex)
        {
            // Auth gate tripped mid-run — pause instead of failing, so the user can
            // re-login and click Resume.
            _logger.LogWarning(sex, "Automation run {RunId} paused — login required", runId);
            await LogAsync(runId, null, "warn",
                $"Login required: {sex.Diagnostics.Notes ?? sex.Diagnostics.State.ToString()} (url: {sex.Diagnostics.Url})");
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
                var run = await db.AutomationRuns.FindAsync(new object[] { runId });
                if (run != null)
                {
                    run.Status = AutomationRunStatus.LoginRequired.ToString();
                    run.Error = sex.Message;
                    // Do NOT set CompletedAt — the run is paused, not finished.
                    await db.SaveChangesAsync();
                }
            }
            catch { /* best effort */ }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Automation run {RunId} failed", runId);
            await LogAsync(runId, null, "error", $"Run failed: {ex.Message}");

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
                var run = await db.AutomationRuns.FindAsync(new object[] { runId });
                if (run != null)
                {
                    run.Status = AutomationRunStatus.Failed.ToString();
                    run.CompletedAt = DateTimeOffset.UtcNow;
                    run.Error = ex.Message;
                    await db.SaveChangesAsync();
                }
            }
            catch { /* best effort */ }
        }
        finally
        {
            _runCts.TryRemove(runId, out _);
        }
    }

    // ── Phase A: search + add to import list ──

    private async Task SearchAndImportProductAsync(
        Guid runId, ShopifyAutomationProduct product, ShopifyAutomationConfig cfg, CancellationToken ct)
    {
        var retries = 0;
        while (retries <= cfg.MaxRetries)
        {
            try
            {
                await UpdateProductAsync(product.Id, p =>
                {
                    p.Status = AutomationProductStatus.Processing.ToString();
                    p.CurrentStep = AutomationStep.NavigatingToYl.ToString();
                    p.UpdatedAt = DateTimeOffset.UtcNow;
                }, ct);

                await UpdateProductAsync(product.Id, p => p.CurrentStep = AutomationStep.Searching.ToString(), ct);

                var query = BuildSearchQuery(product.ProductName);
                await LogAsync(runId, product.Id, "info", $"find-products search: \"{query}\"");

                var candidates = await _adminApp.SearchAsync(cfg, query, ct);
                if (candidates.Count == 0)
                {
                    await UpdateProductAsync(product.Id, p =>
                    {
                        p.Status = AutomationProductStatus.ManualReview.ToString();
                        p.ErrorReason = "find-products returned no candidates";
                        p.UpdatedAt = DateTimeOffset.UtcNow;
                    }, ct);
                    await LogAsync(runId, product.Id, "warn",
                        $"No search results for \"{product.ProductName}\" — marked for manual review");
                    return;
                }

                // Score via the matching engine.
                await UpdateProductAsync(product.Id, p => p.CurrentStep = AutomationStep.Matching.ToString(), ct);

                var brainProduct = await _brain.GetProductAsync(product.BrainProductId, ct);
                var target = new MatchTarget(
                    Title: product.ProductName,
                    Keywords: ExtractKeywords(product.ProductName),
                    MinPrice: brainProduct?.Cost,
                    MaxPrice: brainProduct?.Price,
                    SupplierKey: product.SupplierKey,
                    ImageUrl: brainProduct?.ImageUrls?.FirstOrDefault());

                var matchCandidates = candidates
                    .Select(c => new MatchCandidate(c.ExternalId, c.Title, c.Price, c.Vendor, c.ImageUrl, c.Description))
                    .ToList();

                var bestMatch = _matcher.SelectBest(target, matchCandidates, cfg.MatchConfidenceThreshold);
                if (bestMatch == null)
                {
                    await UpdateProductAsync(product.Id, p =>
                    {
                        p.Status = AutomationProductStatus.ManualReview.ToString();
                        p.ErrorReason = $"No match above threshold ({cfg.MatchConfidenceThreshold})";
                        p.UpdatedAt = DateTimeOffset.UtcNow;
                    }, ct);
                    await LogAsync(runId, product.Id, "warn",
                        $"No match above threshold for \"{product.ProductName}\" — marked for manual review");
                    return;
                }

                var matchResult = _matcher.Score(target, bestMatch);
                await LogAsync(runId, product.Id, "info",
                    $"Best match: \"{bestMatch.Title}\" (confidence: {matchResult.TotalScore:P0})");

                // Click "add to import list".
                await UpdateProductAsync(product.Id, p => p.CurrentStep = AutomationStep.Importing.ToString(), ct);
                await LogAsync(runId, product.Id, "info", $"add-to-import-list: external={bestMatch.Id}");

                var importItemId = await _adminApp.AddToImportListAsync(cfg, bestMatch.Id, ct);

                await UpdateProductAsync(product.Id, p =>
                {
                    p.MatchedResultTitle = bestMatch.Title;
                    p.MatchedExternalId = bestMatch.Id;
                    p.ImportItemId = importItemId;
                    p.Confidence = matchResult.TotalScore;
                    p.ErrorReason = null;
                    p.Status = AutomationProductStatus.Imported.ToString();
                    p.CurrentStep = AutomationStep.NavigatingImportList.ToString();
                    p.UpdatedAt = DateTimeOffset.UtcNow;
                }, ct);

                await LogAsync(runId, product.Id, "info", $"Added to import list (id: {importItemId})");
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (SessionExpiredException)
            {
                // Don't retry — surface upward so the run pauses to LoginRequired.
                throw;
            }
            catch (Exception ex)
            {
                retries++;
                if (retries > cfg.MaxRetries)
                {
                    await UpdateProductAsync(product.Id, p =>
                    {
                        p.Status = AutomationProductStatus.Failed.ToString();
                        p.ErrorReason = ex.Message;
                        p.UpdatedAt = DateTimeOffset.UtcNow;
                    }, ct);
                    await LogAsync(runId, product.Id, "error",
                        $"Failed after {cfg.MaxRetries} retries: {ex.Message}");
                    return;
                }
                var delay = (int)Math.Pow(2, retries) * 500;
                await LogAsync(runId, product.Id, "warn",
                    $"Retry {retries}/{cfg.MaxRetries} in {delay}ms: {ex.Message}");
                await Task.Delay(delay, ct);
            }
        }
    }

    // ── Phase B: push from import-list to store ──

    private async Task PushImportedProductsAsync(Guid runId, ShopifyAutomationConfig cfg, CancellationToken ct)
    {
        await LogAsync(runId, null, "info", "Step 2: Import List → push each product to store");

        List<ShopifyAutomationProduct> imported;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            imported = await db.AutomationProducts
                .Where(p => p.RunId == runId && p.Status == AutomationProductStatus.Imported.ToString())
                .ToListAsync(ct);
        }

        if (imported.Count == 0)
        {
            await LogAsync(runId, null, "info", "Nothing to push — import list is empty for this run");
            return;
        }

        foreach (var product in imported)
        {
            if (ct.IsCancellationRequested) break;

            var importItemId = product.ImportItemId;
            if (string.IsNullOrWhiteSpace(importItemId))
            {
                await UpdateProductAsync(product.Id, p =>
                {
                    p.Status = AutomationProductStatus.Failed.ToString();
                    p.ErrorReason = "Missing import item id — cannot push";
                    p.UpdatedAt = DateTimeOffset.UtcNow;
                }, ct);
                continue;
            }

            await UpdateProductAsync(product.Id, p =>
            {
                p.CurrentStep = AutomationStep.Pushing.ToString();
                p.UpdatedAt = DateTimeOffset.UtcNow;
            }, ct);
            await LogAsync(runId, product.Id, "info", $"push-to-store: import={importItemId}");

            var retries = 0;
            while (retries <= cfg.MaxRetries)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var result = await _adminApp.PushToStoreAsync(cfg, importItemId, ct);
                    if (!result.Success)
                    {
                        throw new Exception(result.Error ?? "unknown push failure");
                    }

                    await UpdateProductAsync(product.Id, p =>
                    {
                        p.ShopifyProductId = result.ShopifyProductId;
                        p.Status = AutomationProductStatus.Pushed.ToString();
                        p.CurrentStep = AutomationStep.Done.ToString();
                        p.UpdatedAt = DateTimeOffset.UtcNow;
                    }, ct);
                    await LogAsync(runId, product.Id, "info",
                        result.ShopifyProductId.HasValue
                            ? $"✓ Pushed to store (Shopify id: {result.ShopifyProductId})"
                            : "✓ Pushed to store");
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (SessionExpiredException) { throw; }
                catch (Exception ex)
                {
                    retries++;
                    if (retries > cfg.MaxRetries)
                    {
                        await UpdateProductAsync(product.Id, p =>
                        {
                            p.Status = AutomationProductStatus.Failed.ToString();
                            p.ErrorReason = ex.Message;
                            p.UpdatedAt = DateTimeOffset.UtcNow;
                        }, ct);
                        await LogAsync(runId, product.Id, "error",
                            $"Push failed after {cfg.MaxRetries} retries: {ex.Message}");
                        break;
                    }
                    var delay = (int)Math.Pow(2, retries) * 500;
                    await LogAsync(runId, product.Id, "warn",
                        $"Push retry {retries}/{cfg.MaxRetries} in {delay}ms: {ex.Message}");
                    await Task.Delay(delay, ct);
                }
            }
        }
    }

    // ── helpers ──

    internal static string BuildSearchQuery(string productName)
    {
        var kws = ExtractKeywords(productName);
        // Use the top ~4 tokens to keep the query focused; the app's search ranks fuzzily anyway.
        return kws.Length <= 4 ? string.Join(" ", kws) : string.Join(" ", kws.Take(4));
    }

    internal static string[] ExtractKeywords(string productName)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "or", "for", "with", "in", "on", "at", "to", "of", "is", "by"
        };
        return productName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .ToArray();
    }

    private async Task UpdateProductAsync(Guid productId, Action<ShopifyAutomationProduct> mutate, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var row = await db.AutomationProducts.FindAsync(new object[] { productId }, ct);
        if (row == null) return;
        mutate(row);
        await db.SaveChangesAsync(ct);
    }

    private async Task LogAsync(Guid runId, Guid? productId, string level, string message)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            db.AutomationLogs.Add(new ShopifyAutomationLog
            {
                RunId = runId,
                ProductId = productId,
                Level = level,
                Message = message
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write automation log");
        }
    }

    private static ShopifyAutomationConfig Clone(ShopifyAutomationConfig c) => new()
    {
        Id = c.Id,
        ShopifyStoreUrl = c.ShopifyStoreUrl,
        FindProductsUrl = c.FindProductsUrl,
        ImportListUrl = c.ImportListUrl,
        AuthMode = c.AuthMode,
        MaxRetries = c.MaxRetries,
        MatchConfidenceThreshold = c.MatchConfidenceThreshold,
        HeadlessMode = c.HeadlessMode,
        UseApiFirst = c.UseApiFirst,
        SessionCookie = c.SessionCookie,
        AuthToken = c.AuthToken,
        UpdatedAt = c.UpdatedAt,
    };

    // ── Mappers ──

    private static AutomationConfigDto MapConfig(ShopifyAutomationConfig c) => new(
        c.ShopifyStoreUrl, c.FindProductsUrl, c.ImportListUrl,
        c.AppUrl, c.ShopifyApiKey, c.ShopifyHost, c.DefaultSearch,
        c.AuthMode, c.MaxRetries,
        c.MatchConfidenceThreshold, c.HeadlessMode, c.UseApiFirst,
        c.SessionCookie != null ? "***" : null,
        c.AuthToken != null ? "***" : null);

    private static AutomationRunDto MapRun(ShopifyAutomationRun r) => new(
        r.Id, r.Status, r.TotalProducts, r.ProcessedCount,
        r.ImportedCount, r.PushedCount, r.FailedCount,
        r.StartedAt, r.CompletedAt, r.Error);

    private static AutomationProductDto MapProduct(ShopifyAutomationProduct p) => new(
        p.Id, p.RunId, p.BrainProductId, p.ProductName, p.SupplierKey,
        p.Status, p.CurrentStep, p.MatchedResultTitle, p.Confidence,
        p.ErrorReason, p.ShopifyProductId, p.UpdatedAt);
}
