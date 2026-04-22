using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FluentAssertions;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.StoreManagement.Domain;
using AutoCommerce.StoreManagement.Infrastructure;
using AutoCommerce.StoreManagement.Services;

namespace AutoCommerce.StoreManagement.Tests;

public class ShopifyAutomationServiceTests : IAsyncLifetime
{
    private ServiceProvider _sp = null!;
    private IServiceScopeFactory _scopes = null!;
    private StubBrainClient _brain = null!;
    private FakeAdminAppClient _admin = null!;
    private ShopifyAutomationService _sut = null!;

    public Task InitializeAsync()
    {
        var services = new ServiceCollection();
        var dbName = $"AutomationTests-{Guid.NewGuid()}";
        services.AddDbContext<StoreDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddLogging();
        _sp = services.BuildServiceProvider();

        // Seed DB.
        using (var s = _sp.CreateScope())
        {
            s.ServiceProvider.GetRequiredService<StoreDbContext>().Database.EnsureCreated();
        }

        _scopes = _sp.GetRequiredService<IServiceScopeFactory>();
        _brain = new StubBrainClient();
        _admin = new FakeAdminAppClient();

        _sut = new ShopifyAutomationService(
            _scopes,
            _brain,
            new ProductMatchingEngine(),
            _admin,
            NullLogger<ShopifyAutomationService>.Instance);

        return Task.CompletedTask;
    }

    public Task DisposeAsync() { _sp.Dispose(); return Task.CompletedTask; }

    private async Task<StoreDbContext> NewDbAsync()
    {
        var scope = _sp.CreateScope();
        return await Task.FromResult(scope.ServiceProvider.GetRequiredService<StoreDbContext>());
    }

    private static ProductResponse MakeProduct(Guid id, string title = "Wireless Bluetooth Headphones", decimal price = 49.99m)
    {
        var supplier = new SupplierListing("s1", "ext-1", 20m, "USD", 5, 4.5, 10, "http://supplier");
        return new ProductResponse(
            id, "ext-1", title, "Audio", "Great sound",
            new[] { "http://img/1.jpg" }, new[] { "bluetooth", "headphones" },
            "US", 0.9, 20m, price, 50, "Active", "s1",
            new[] { supplier }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private async Task SeedConfigAsync(
        string findUrl = "https://admin.shopify.com/store/buydiro/apps/dropshipper-ai/app/find-products",
        string importUrl = "https://admin.shopify.com/store/buydiro/apps/dropshipper-ai/app/import-list")
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        db.AutomationConfigs.Add(new ShopifyAutomationConfig
        {
            FindProductsUrl = findUrl,
            ImportListUrl = importUrl,
            MaxRetries = 1,
            MatchConfidenceThreshold = 0.3
        });
        await db.SaveChangesAsync();
    }

    // ── Query projection regression: MapProduct/MapRun were being pushed into SQL ──

    [Fact]
    public async Task GetRunProductsAsync_MaterializesBeforeMapping_DoesNotThrowTranslationException()
    {
        var runId = Guid.NewGuid();
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            db.AutomationRuns.Add(new ShopifyAutomationRun { Id = runId, Status = "Running" });
            db.AutomationProducts.Add(new ShopifyAutomationProduct
            {
                RunId = runId, BrainProductId = Guid.NewGuid(), ProductName = "Item A"
            });
            await db.SaveChangesAsync();
        }

        var products = await _sut.GetRunProductsAsync(runId, CancellationToken.None);

        products.Should().HaveCount(1);
        products[0].ProductName.Should().Be("Item A");
    }

    [Fact]
    public async Task GetRunsAsync_MaterializesBeforeMapping_DoesNotThrowTranslationException()
    {
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            db.AutomationRuns.Add(new ShopifyAutomationRun { Status = "Completed" });
            db.AutomationRuns.Add(new ShopifyAutomationRun { Status = "Running" });
            await db.SaveChangesAsync();
        }

        var runs = await _sut.GetRunsAsync(10, CancellationToken.None);
        runs.Should().HaveCount(2);
    }

    // ── Happy path: full three‑step flow ──

    [Fact]
    public async Task ExecuteRunAsync_HappyPath_SearchesAddsAndPushes()
    {
        await SeedConfigAsync();
        var brainId = Guid.NewGuid();
        _brain.Products[brainId] = MakeProduct(brainId);

        _admin.SearchResults["Wireless Bluetooth Headphones"] = new List<AdminAppSearchCandidate>
        {
            new("ext-match-1", "Wireless Bluetooth Headphones", 45m, "s1", null, "matching desc"),
            new("ext-match-2", "Wired Headphones", 15m, "s1", null, null)
        };
        _admin.PushShopifyId = 900001;

        var runId = await StartAndExecuteRunAsync(brainId);

        _admin.SearchCalls.Should().NotBeEmpty();
        _admin.AddedExternalIds.Should().Contain("ext-match-1");
        _admin.PushedImportItemIds.Should().Contain(id => id.StartsWith("imp-") || id == "ext-match-1");

        var products = await _sut.GetRunProductsAsync(runId, CancellationToken.None);
        products.Should().HaveCount(1);
        products[0].Status.Should().Be("Pushed");
        products[0].ShopifyProductId.Should().Be(900001);
        products[0].MatchedResultTitle.Should().Be("Wireless Bluetooth Headphones");

        var run = await _sut.GetRunAsync(runId, CancellationToken.None);
        run!.Status.Should().Be("Completed");
        run.PushedCount.Should().Be(1);
        run.FailedCount.Should().Be(0);
    }

    // ── Search returns zero results → ManualReview, no add/push attempted ──

    [Fact]
    public async Task ExecuteRunAsync_NoSearchResults_MarksManualReview()
    {
        await SeedConfigAsync();
        var brainId = Guid.NewGuid();
        _brain.Products[brainId] = MakeProduct(brainId, title: "Obscure Item XYZ");
        _admin.SearchResults["Obscure Item XYZ"] = new List<AdminAppSearchCandidate>();

        var runId = await StartAndExecuteRunAsync(brainId);

        _admin.AddedExternalIds.Should().BeEmpty();
        _admin.PushedImportItemIds.Should().BeEmpty();

        var products = await _sut.GetRunProductsAsync(runId, CancellationToken.None);
        products[0].Status.Should().Be("ManualReview");
        products[0].ErrorReason.Should().Contain("no candidates");
    }

    // ── Match below threshold → ManualReview ──

    [Fact]
    public async Task ExecuteRunAsync_NoMatchAboveThreshold_MarksManualReview()
    {
        // Threshold high enough that unrelated candidates don't pass.
        await SeedConfigAsync();
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var cfg = await db.AutomationConfigs.FirstAsync();
            cfg.MatchConfidenceThreshold = 0.95;
            await db.SaveChangesAsync();
        }
        var brainId = Guid.NewGuid();
        _brain.Products[brainId] = MakeProduct(brainId, title: "Red Running Shoe");
        _admin.SearchResults["Red Running Shoe"] = new List<AdminAppSearchCandidate>
        {
            new("ext-1", "Totally Unrelated Thing", 99m, null, null, null)
        };

        var runId = await StartAndExecuteRunAsync(brainId);
        var products = await _sut.GetRunProductsAsync(runId, CancellationToken.None);
        products[0].Status.Should().Be("ManualReview");
        _admin.AddedExternalIds.Should().BeEmpty();
    }

    // ── Push fails once then succeeds via retry ──

    [Fact]
    public async Task ExecuteRunAsync_PushFailsThenSucceeds_RetriesAndMarksPushed()
    {
        await SeedConfigAsync();
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var cfg = await db.AutomationConfigs.FirstAsync();
            cfg.MaxRetries = 2;
            await db.SaveChangesAsync();
        }
        var brainId = Guid.NewGuid();
        _brain.Products[brainId] = MakeProduct(brainId);
        _admin.SearchResults["Wireless Bluetooth Headphones"] = new List<AdminAppSearchCandidate>
        {
            new("ext-1", "Wireless Bluetooth Headphones", 49m, "s1", null, null)
        };
        _admin.PushShopifyId = 1234;
        _admin.PushFailuresRemaining = 1;

        var runId = await StartAndExecuteRunAsync(brainId);

        _admin.PushCallCount.Should().Be(2);
        var products = await _sut.GetRunProductsAsync(runId, CancellationToken.None);
        products[0].Status.Should().Be("Pushed");
        products[0].ShopifyProductId.Should().Be(1234);
    }

    // ── Push fails past retry budget → Failed ──

    [Fact]
    public async Task ExecuteRunAsync_PushExhaustsRetries_MarksFailed()
    {
        await SeedConfigAsync();
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var cfg = await db.AutomationConfigs.FirstAsync();
            cfg.MaxRetries = 0;
            await db.SaveChangesAsync();
        }
        var brainId = Guid.NewGuid();
        _brain.Products[brainId] = MakeProduct(brainId);
        _admin.SearchResults["Wireless Bluetooth Headphones"] = new List<AdminAppSearchCandidate>
        {
            new("ext-1", "Wireless Bluetooth Headphones", 49m, "s1", null, null)
        };
        _admin.PushAlwaysFails = true;

        var runId = await StartAndExecuteRunAsync(brainId);

        var products = await _sut.GetRunProductsAsync(runId, CancellationToken.None);
        products[0].Status.Should().Be("Failed");
        products[0].ErrorReason.Should().NotBeNullOrWhiteSpace();
    }

    // ── Import list count increases between baseline and after-adds ──

    [Fact]
    public async Task ExecuteRunAsync_ImportListCountIncreases_FromBaselineToAfterAdds()
    {
        await SeedConfigAsync();
        // Pre-seed the fake import list with 2 items — matches the "starts with 2" case
        // from the requirement.
        _admin.SeedImportListItem("existing-1");
        _admin.SeedImportListItem("existing-2");

        var id1 = Guid.NewGuid(); var id2 = Guid.NewGuid();
        _brain.Products[id1] = MakeProduct(id1, title: "Alpha Widget");
        _brain.Products[id2] = MakeProduct(id2, title: "Beta Gadget");
        _admin.SearchResults["Alpha Widget"] = new List<AdminAppSearchCandidate>
        {
            new("a1", "Alpha Widget", 20m, null, null, null)
        };
        _admin.SearchResults["Beta Gadget"] = new List<AdminAppSearchCandidate>
        {
            new("b1", "Beta Gadget", 30m, null, null, null)
        };

        var baselineList = await _admin.GetImportListAsync(
            new ShopifyAutomationConfig(), CancellationToken.None);
        baselineList.Should().HaveCount(2, "the import list should start at 2 items");

        var runId = await StartAndExecuteRunAsync(id1, id2);

        var afterList = await _admin.GetImportListAsync(
            new ShopifyAutomationConfig(), CancellationToken.None);
        afterList.Count.Should().BeGreaterThan(2, "adding 2 products to an import list of 2 must raise the count");
        afterList.Count.Should().Be(4);

        var logs = await _sut.GetRunLogsAsync(runId, 100, CancellationToken.None);
        logs.Should().Contain(l => l.Message.Contains("baseline: 2"));
        logs.Should().Contain(l => l.Message.Contains("Import list grew: 2 → 4"));
    }

    [Fact]
    public async Task ExecuteRunAsync_ImportListDoesNotGrow_LogsWarning()
    {
        await SeedConfigAsync();
        _admin.SeedImportListItem("existing-1");
        _admin.SuppressAddsFromImportList = true; // simulate silent add failures

        var id1 = Guid.NewGuid();
        _brain.Products[id1] = MakeProduct(id1, title: "Alpha Widget");
        _admin.SearchResults["Alpha Widget"] = new List<AdminAppSearchCandidate>
        {
            new("a1", "Alpha Widget", 20m, null, null, null)
        };

        var runId = await StartAndExecuteRunAsync(id1);

        var logs = await _sut.GetRunLogsAsync(runId, 100, CancellationToken.None);
        logs.Should().Contain(l =>
            l.Level == "warn" && l.Message.Contains("did NOT increase"));
    }

    // ── Phase ordering: all adds happen before first push ──

    [Fact]
    public async Task ExecuteRunAsync_AddsAllBeforePushingAny()
    {
        await SeedConfigAsync();
        var id1 = Guid.NewGuid(); var id2 = Guid.NewGuid();
        _brain.Products[id1] = MakeProduct(id1, title: "Alpha Widget");
        _brain.Products[id2] = MakeProduct(id2, title: "Beta Gadget");
        _admin.SearchResults["Alpha Widget"] = new List<AdminAppSearchCandidate>
        {
            new("a1", "Alpha Widget", 20m, null, null, null)
        };
        _admin.SearchResults["Beta Gadget"] = new List<AdminAppSearchCandidate>
        {
            new("b1", "Beta Gadget", 30m, null, null, null)
        };

        await StartAndExecuteRunAsync(id1, id2);

        // Both adds must precede the first push.
        var firstPushIdx = _admin.CallLog.FindIndex(s => s.StartsWith("push:"));
        var lastAddIdx = _admin.CallLog.FindLastIndex(s => s.StartsWith("add:"));
        firstPushIdx.Should().BeGreaterThan(lastAddIdx);
        _admin.AddedExternalIds.Should().BeEquivalentTo(new[] { "a1", "b1" });
    }

    // ── StartRunAsync guards ──

    [Fact]
    public async Task StartRunAsync_NoConfig_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.StartRunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartRunAsync_NoProductsInBrain_Throws()
    {
        await SeedConfigAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.StartRunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartRunAsync_NoFindProductsUrl_Throws()
    {
        await SeedConfigAsync(findUrl: "");
        _brain.Products[Guid.NewGuid()] = MakeProduct(Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.StartRunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartRunAsync_NoImportListUrl_Throws()
    {
        await SeedConfigAsync(importUrl: "");
        _brain.Products[Guid.NewGuid()] = MakeProduct(Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.StartRunAsync(CancellationToken.None));
    }

    // ── Helpers ──

    private async Task<Guid> StartAndExecuteRunAsync(params Guid[] brainProductIds)
    {
        // StartRunAsync kicks off the background task, but we run ExecuteRunAsync inline
        // so the test can await completion deterministically.
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var cfg = await db.AutomationConfigs.FirstAsync();

        var products = await _brain.GetProductsAsync("active", CancellationToken.None);
        var run = new ShopifyAutomationRun
        {
            Status = AutomationRunStatus.Running.ToString(),
            TotalProducts = products.Count
        };
        db.AutomationRuns.Add(run);
        foreach (var p in products)
        {
            db.AutomationProducts.Add(new ShopifyAutomationProduct
            {
                RunId = run.Id,
                BrainProductId = p.Id,
                ProductName = p.Title,
                SupplierKey = p.SupplierKey,
                Status = AutomationProductStatus.Ready.ToString()
            });
        }
        await db.SaveChangesAsync();

        await _sut.ExecuteRunAsync(run.Id, cfg, CancellationToken.None);
        return run.Id;
    }
}

// ── Fake admin‑app client with scriptable behaviour ──

internal class FakeAdminAppClient : IShopifyAdminAppClient
{
    public Dictionary<string, List<AdminAppSearchCandidate>> SearchResults { get; } = new();
    public List<string> SearchCalls { get; } = new();
    public List<string> AddedExternalIds { get; } = new();
    public List<string> PushedImportItemIds { get; } = new();
    public List<string> CallLog { get; } = new();
    public long? PushShopifyId { get; set; }
    public int PushFailuresRemaining { get; set; }
    public bool PushAlwaysFails { get; set; }
    public int PushCallCount { get; private set; }

    // Pre-existing items in the import list (before the run starts).
    private readonly List<string> _seededImportIds = new();
    public bool SuppressAddsFromImportList { get; set; }
    public void SeedImportListItem(string externalId) => _seededImportIds.Add(externalId);

    public Task<IReadOnlyList<AdminAppSearchCandidate>> SearchAsync(
        ShopifyAutomationConfig config, string query, CancellationToken ct)
    {
        SearchCalls.Add(query);
        CallLog.Add($"search:{query}");
        // Look up by exact query first, then by any key that shares a token.
        if (SearchResults.TryGetValue(query, out var exact))
            return Task.FromResult<IReadOnlyList<AdminAppSearchCandidate>>(exact);
        foreach (var (key, val) in SearchResults)
            if (key.Contains(query, StringComparison.OrdinalIgnoreCase) || query.Contains(key, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<IReadOnlyList<AdminAppSearchCandidate>>(val);
        return Task.FromResult<IReadOnlyList<AdminAppSearchCandidate>>(Array.Empty<AdminAppSearchCandidate>());
    }

    public Task<string> AddToImportListAsync(ShopifyAutomationConfig config, string externalId, CancellationToken ct)
    {
        AddedExternalIds.Add(externalId);
        CallLog.Add($"add:{externalId}");
        return Task.FromResult($"imp-{externalId}");
    }

    public Task<IReadOnlyList<AdminAppImportListItem>> GetImportListAsync(ShopifyAutomationConfig config, CancellationToken ct)
    {
        var items = new List<AdminAppImportListItem>();
        foreach (var e in _seededImportIds)
            items.Add(new AdminAppImportListItem($"imp-{e}", e, e));
        if (!SuppressAddsFromImportList)
            foreach (var e in AddedExternalIds)
                items.Add(new AdminAppImportListItem($"imp-{e}", e, e));
        return Task.FromResult<IReadOnlyList<AdminAppImportListItem>>(items);
    }

    public Task<AdminAppPushResult> PushToStoreAsync(ShopifyAutomationConfig config, string importItemId, CancellationToken ct)
    {
        PushCallCount++;
        PushedImportItemIds.Add(importItemId);
        CallLog.Add($"push:{importItemId}");

        if (PushAlwaysFails)
            return Task.FromResult(new AdminAppPushResult(false, null, "scripted push failure"));
        if (PushFailuresRemaining > 0)
        {
            PushFailuresRemaining--;
            return Task.FromResult(new AdminAppPushResult(false, null, "transient push failure"));
        }
        return Task.FromResult(new AdminAppPushResult(true, PushShopifyId, null));
    }
}
