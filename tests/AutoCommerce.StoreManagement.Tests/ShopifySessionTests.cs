using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FluentAssertions;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.StoreManagement.Domain;
using AutoCommerce.StoreManagement.Infrastructure;
using AutoCommerce.StoreManagement.Services;

namespace AutoCommerce.StoreManagement.Tests;

// ── 1. LoginStateDetector classifier (pure) ──

public class LoginStateDetectorClassifierTests
{
    private const string Target = "https://admin.shopify.com/store/buydiro/apps/dropshipper-ai/app/find-products";

    [Theory]
    // Login / account-picker detection
    [InlineData("https://accounts.shopify.com/login", false, false, false, LoginState.LoginPage)]
    [InlineData("https://accounts.shopify.com/lookup?rid=abc&verify=xyz", false, false, false, LoginState.LoginPage)]
    [InlineData("https://admin.shopify.com/account/login", false, false, false, LoginState.LoginPage)]
    [InlineData("https://some.site/auth/login", false, false, false, LoginState.LoginPage)]
    [InlineData("https://somewhere.example.com/", true, true, false, LoginState.LoginPage)]
    // Store picker: was a false-positive for "Authenticated" before the tightening —
    // this is the bug users hit when their cookies aren't scoped to the target store.
    [InlineData("https://admin.shopify.com/", false, false, false, LoginState.AccountSelection)]
    [InlineData("https://admin.shopify.com/store", false, false, false, LoginState.AccountSelection)]
    [InlineData("https://admin.shopify.com/store?redirect=x", false, false, false, LoginState.AccountSelection)]
    [InlineData("https://admin.shopify.com/store/x/apps/y", false, false, true, LoginState.AccountSelection)]
    // Authenticated only when landed URL matches the target app path
    [InlineData("https://admin.shopify.com/store/buydiro/apps/dropshipper-ai/app/find-products", false, false, false, LoginState.Authenticated)]
    [InlineData("https://admin.shopify.com/store/buydiro/apps/dropshipper-ai/app/import-list", false, false, false, LoginState.Authenticated)] // same app, diff page — still OK
    // Inside admin.shopify.com but WRONG store/app → NotInApp, not Authenticated
    [InlineData("https://admin.shopify.com/store/somebody-else/apps/dropshipper-ai/app/find-products", false, false, false, LoginState.NotInApp)]
    [InlineData("https://admin.shopify.com/store/buydiro/settings", false, false, false, LoginState.NotInApp)]
    // Clearly external / empty
    [InlineData("https://example.com/something", false, false, false, LoginState.NotInApp)]
    [InlineData("", false, false, false, LoginState.Unknown)]
    public void Classify_WithTargetUrl_ReturnsExpectedState(string url, bool email, bool password, bool chooser, LoginState expected)
    {
        var (state, _, _) = LoginStateDetector.Classify(url, Target, email, password, chooser);
        state.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://admin.shopify.com/store/buydiro/apps/dropshipper-ai/app/find-products", true)]
    [InlineData("https://admin.shopify.com/store/buydiro/apps/dropshipper-ai/app/import-list", true)]
    [InlineData("https://admin.shopify.com/store/different/apps/dropshipper-ai/app/find-products", false)]
    [InlineData("https://admin.shopify.com/store/buydiro/settings", false)]
    public void IsOnTargetPath_ChecksStoreAndAppPrefix(string url, bool expected)
    {
        LoginStateDetector.IsOnTargetPath(url.ToLowerInvariant(), Target).Should().Be(expected);
    }
}

// ── 2. Service marks run LoginRequired on SessionExpiredException ──

public class ShopifyAutomationService_LoginRequiredTests : IAsyncLifetime
{
    private ServiceProvider _sp = null!;
    private StubBrainClient _brain = null!;
    private ScriptedAdminAppClient _admin = null!;
    private ShopifyAutomationService _sut = null!;

    public Task InitializeAsync()
    {
        var services = new ServiceCollection();
        var dbName = $"LoginReqTests-{Guid.NewGuid()}";
        services.AddDbContext<StoreDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddLogging();
        _sp = services.BuildServiceProvider();
        using (var s = _sp.CreateScope())
            s.ServiceProvider.GetRequiredService<StoreDbContext>().Database.EnsureCreated();

        _brain = new StubBrainClient();
        _admin = new ScriptedAdminAppClient();
        _sut = new ShopifyAutomationService(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            _brain,
            new ProductMatchingEngine(),
            _admin,
            NullLogger<ShopifyAutomationService>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() { _sp.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task Run_WhenSearchThrowsSessionExpired_TransitionsToLoginRequired_NoRetries()
    {
        await SeedConfigAsync(maxRetries: 3);
        var id = Guid.NewGuid();
        _brain.Products[id] = MakeProduct(id, title: "Alpha Widget");
        _admin.ThrowOnSearch = () => new SessionExpiredException(
            new LoginDiagnostics(LoginState.LoginPage, "https://accounts.shopify.com/login",
                "Login", true, true, true, false, false, false, "login page detected"));

        var runId = await StartAndExecuteRunAsync(id);

        var run = await _sut.GetRunAsync(runId, CancellationToken.None);
        run!.Status.Should().Be("LoginRequired", "session-expired mid-run must pause, not fail");
        run.CompletedAt.Should().BeNull("a paused run is not finished");
        _admin.SearchCallCount.Should().Be(1,
            "retry loop must NOT keep hammering a session-expired call — that's the bug we're guarding against");

        var logs = await _sut.GetRunLogsAsync(runId, 100, CancellationToken.None);
        logs.Should().Contain(l => l.Level == "warn" && l.Message.Contains("Login required"));
    }

    [Fact]
    public async Task Run_WhenPushThrowsSessionExpired_TransitionsToLoginRequired()
    {
        await SeedConfigAsync(maxRetries: 3);
        var id = Guid.NewGuid();
        _brain.Products[id] = MakeProduct(id);
        _admin.SearchResults["Wireless Bluetooth Headphones"] = new List<AdminAppSearchCandidate>
        {
            new("ext-1", "Wireless Bluetooth Headphones", 49m, "s1", null, null)
        };
        _admin.ThrowOnPush = () => new SessionExpiredException(
            new LoginDiagnostics(LoginState.LoginPage, "https://accounts.shopify.com/login",
                "Login", true, true, true, false, false, false, "expired"));

        var runId = await StartAndExecuteRunAsync(id);

        var run = await _sut.GetRunAsync(runId, CancellationToken.None);
        run!.Status.Should().Be("LoginRequired");
        _admin.PushCallCount.Should().Be(1, "push must not retry after session-expired");
    }

    // ── 3. Resume flow ──

    [Fact]
    public async Task Resume_WithValidSession_ContinuesRunToCompletion()
    {
        await SeedConfigAsync(maxRetries: 0);
        var id = Guid.NewGuid();
        _brain.Products[id] = MakeProduct(id, title: "Alpha Widget");
        _admin.SearchResults["Alpha Widget"] = new List<AdminAppSearchCandidate>
        {
            new("ext-1", "Alpha Widget", 20m, null, null, null)
        };

        // First run: search throws, run pauses.
        _admin.ThrowOnSearch = () => new SessionExpiredException(
            new LoginDiagnostics(LoginState.LoginPage, "x", "x", true, true, true, false, false, false, "expired"));
        var runId = await StartAndExecuteRunAsync(id);
        (await _sut.GetRunAsync(runId, CancellationToken.None))!.Status.Should().Be("LoginRequired");

        // "User logs in" — clear the throw and resume. Use the session-manager-less overload
        // so ResumeRunAsync doesn't insist on an IShopifySessionManager.
        _admin.ThrowOnSearch = null;
        var resumed = await _sut.ResumeRunAsync(runId, CancellationToken.None);
        await WaitForStatusAsync(runId, "Completed");

        var finalRun = await _sut.GetRunAsync(runId, CancellationToken.None);
        finalRun!.Status.Should().Be("Completed");
        finalRun.PushedCount.Should().Be(1);
    }

    [Fact]
    public async Task Resume_WhenRunNotPaused_Throws()
    {
        await SeedConfigAsync();
        var id = Guid.NewGuid();
        _brain.Products[id] = MakeProduct(id);
        _admin.SearchResults["Wireless Bluetooth Headphones"] = new List<AdminAppSearchCandidate>
        {
            new("ext-1", "Wireless Bluetooth Headphones", 49m, "s1", null, null)
        };
        var runId = await StartAndExecuteRunAsync(id);
        var run = await _sut.GetRunAsync(runId, CancellationToken.None);
        run!.Status.Should().Be("Completed");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ResumeRunAsync(runId, CancellationToken.None));
    }

    // ── helpers ──

    private async Task SeedConfigAsync(int maxRetries = 1)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        db.AutomationConfigs.Add(new ShopifyAutomationConfig
        {
            FindProductsUrl = "https://admin.shopify.com/store/x/apps/y/app/find-products",
            ImportListUrl = "https://admin.shopify.com/store/x/apps/y/app/import-list",
            MaxRetries = maxRetries,
            MatchConfidenceThreshold = 0.3
        });
        await db.SaveChangesAsync();
    }

    private static ProductResponse MakeProduct(Guid id, string title = "Wireless Bluetooth Headphones", decimal price = 49.99m)
    {
        var supplier = new SupplierListing("s1", "ext-1", 20m, "USD", 5, 4.5, 10, "http://supplier");
        return new ProductResponse(
            id, "ext-1", title, "Audio", "desc",
            new[] { "http://img/1.jpg" }, new[] { "bluetooth" },
            "US", 0.9, 20m, price, 50, "Active", "s1",
            new[] { supplier }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private async Task<Guid> StartAndExecuteRunAsync(params Guid[] _)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var cfg = await db.AutomationConfigs.FirstAsync();
        var products = await _brain.GetProductsAsync("active", CancellationToken.None);
        var run = new ShopifyAutomationRun { Status = "Running", TotalProducts = products.Count };
        db.AutomationRuns.Add(run);
        foreach (var p in products)
            db.AutomationProducts.Add(new ShopifyAutomationProduct
            {
                RunId = run.Id, BrainProductId = p.Id, ProductName = p.Title,
                SupplierKey = p.SupplierKey, Status = "Ready"
            });
        await db.SaveChangesAsync();
        await _sut.ExecuteRunAsync(run.Id, cfg, CancellationToken.None);
        return run.Id;
    }

    private async Task WaitForStatusAsync(Guid runId, string status, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var run = await _sut.GetRunAsync(runId, CancellationToken.None);
            if (run?.Status == status) return;
            await Task.Delay(50);
        }
    }
}

internal class ScriptedAdminAppClient : IShopifyAdminAppClient
{
    public Dictionary<string, List<AdminAppSearchCandidate>> SearchResults { get; } = new();
    public Func<Exception>? ThrowOnSearch { get; set; }
    public Func<Exception>? ThrowOnPush { get; set; }
    public int SearchCallCount { get; private set; }
    public int PushCallCount { get; private set; }

    public Task<IReadOnlyList<AdminAppSearchCandidate>> SearchAsync(
        ShopifyAutomationConfig config, string query, CancellationToken ct)
    {
        SearchCallCount++;
        if (ThrowOnSearch != null) throw ThrowOnSearch();
        if (SearchResults.TryGetValue(query, out var exact))
            return Task.FromResult<IReadOnlyList<AdminAppSearchCandidate>>(exact);
        foreach (var (k, v) in SearchResults)
            if (k.Contains(query, StringComparison.OrdinalIgnoreCase) || query.Contains(k, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<IReadOnlyList<AdminAppSearchCandidate>>(v);
        return Task.FromResult<IReadOnlyList<AdminAppSearchCandidate>>(Array.Empty<AdminAppSearchCandidate>());
    }

    public Task<string> AddToImportListAsync(ShopifyAutomationConfig c, string externalId, CancellationToken ct) =>
        Task.FromResult($"imp-{externalId}");

    public Task<IReadOnlyList<AdminAppImportListItem>> GetImportListAsync(ShopifyAutomationConfig c, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AdminAppImportListItem>>(Array.Empty<AdminAppImportListItem>());

    public Task<AdminAppPushResult> PushToStoreAsync(ShopifyAutomationConfig c, string importItemId, CancellationToken ct)
    {
        PushCallCount++;
        if (ThrowOnPush != null) throw ThrowOnPush();
        return Task.FromResult(new AdminAppPushResult(true, 12345, null));
    }
}

// ── 4. SessionManager basic disk round-trip (upload + read status) ──

public class ShopifySessionManagerTests : IAsyncLifetime
{
    private string _dir = null!;
    private ShopifySessionManager _sut = null!;

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"session-mgr-{Guid.NewGuid()}");
        Directory.CreateDirectory(_dir);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("Automation:StateDir", _dir) })
            .Build();
        _sut = new ShopifySessionManager(NullLogger<ShopifySessionManager>.Instance, cfg);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetStatusAsync_NoStateFile_ReturnsLoginRequired()
    {
        var status = await _sut.GetStatusAsync(CancellationToken.None);
        status.StorageStateExists.Should().BeFalse();
        status.State.Should().Be("login_required");
    }

    [Fact]
    public async Task ImportStorageStateAsync_AcceptsStorageStateObject_WithAuthCookie_ReturnsConnected()
    {
        // Must include a recognised auth cookie (e.g. koa.sid, _secure_admin_session_id_*)
        // for the import to return "connected" — tracker cookies alone aren't enough.
        var stateJson = """{"cookies":[{"name":"koa.sid","value":"abc","domain":"admin.shopify.com","path":"/"},{"name":"_shopify_y","value":"1","domain":".shopify.com","path":"/"}],"origins":[]}""";
        var status = await _sut.ImportStorageStateAsync(stateJson, CancellationToken.None);
        status.State.Should().Be("connected");
        status.StorageStateExists.Should().BeTrue();
        File.Exists(_sut.StorageStatePath).Should().BeTrue();
    }

    [Fact]
    public async Task ImportStorageStateAsync_CookieArrayWithoutAuthCookie_ReturnsLoginRequired()
    {
        // The 3 tracker cookies from shopify.com with no admin-session cookie: Import must
        // NOT say "connected" — that was the false positive that misled the user earlier.
        var arrayJson = """[{"name":"_shopify_y","value":"1","domain":".shopify.com","path":"/"}]""";
        var status = await _sut.ImportStorageStateAsync(arrayJson, CancellationToken.None);
        status.State.Should().Be("login_required");
        status.Message.Should().Contain("auth");

        var saved = await File.ReadAllTextAsync(_sut.StorageStatePath);
        saved.Should().Contain("\"cookies\"");
    }

    [Fact]
    public async Task ImportStorageStateAsync_AcceptsCookieArrayWithKoaSid_ReturnsConnected()
    {
        var arrayJson = """[{"name":"koa.sid","value":"abc","domain":"admin.shopify.com","path":"/"}]""";
        var status = await _sut.ImportStorageStateAsync(arrayJson, CancellationToken.None);
        status.State.Should().Be("connected");
    }

    [Fact]
    public async Task ImportStorageStateAsync_InvalidJson_ReturnsError()
    {
        var status = await _sut.ImportStorageStateAsync("not-json", CancellationToken.None);
        status.State.Should().Be("error");
        status.Message.Should().Contain("Invalid JSON");
    }
}
