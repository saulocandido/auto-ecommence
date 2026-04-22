using System.Text.Json;
using Microsoft.Playwright;
using AutoCommerce.StoreManagement.Domain;

namespace AutoCommerce.StoreManagement.Services;

/// <summary>
/// Drives the Shopify admin "Dropshipper AI" app through a real Chromium browser,
/// reusing the session owned by <see cref="IShopifySessionManager"/>.
///
/// CRITICAL: every step calls <see cref="LoginStateDetector.DetectAsync"/> BEFORE
/// it tries to operate on the page. If the detector says the page is a login /
/// account-picker, the step throws <see cref="SessionExpiredException"/> instead of
/// retrying a selector (e.g. the search input) that will never appear. The caller
/// (<see cref="ShopifyAutomationService"/>) maps this to <c>Run.Status = "LoginRequired"</c>.
/// </summary>
public class PlaywrightShopifyAdminAppClient : IShopifyAdminAppClient, IAsyncDisposable
{
    private readonly IShopifySessionManager _sessions;
    private readonly ILogger<PlaywrightShopifyAdminAppClient> _logger;
    private readonly string _screenshotDir;

    private IPlaywright? _pw;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _browserLock = new(1, 1);

    public PlaywrightShopifyAdminAppClient(
        IShopifySessionManager sessions,
        ILogger<PlaywrightShopifyAdminAppClient> logger)
    {
        _sessions = sessions;
        _logger = logger;
        _screenshotDir = Path.Combine(Path.GetTempPath(), "autocommerce-automation");
        Directory.CreateDirectory(_screenshotDir);
    }

    // ── search ──

    public async Task<IReadOnlyList<AdminAppSearchCandidate>> SearchAsync(
        ShopifyAutomationConfig config, string query, CancellationToken ct)
    {
        await using var session = await OpenSessionAsync(config, ct);
        try
        {
            var findUrl = config.FindProductsUrl;
            _logger.LogInformation("Navigating to {Url}", findUrl);
            await session.Page.GotoAsync(findUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            await session.Page.WaitForTimeoutAsync(1500);

            // ── AUTH GATE ──
            await EnsureAuthenticatedAsync(session.Page, ct);

            var searchBox = await session.Page.WaitForSelectorAsync(
                SelectorList("input[type='search']",
                             "input[placeholder*='Search' i]",
                             "[role='searchbox']",
                             "input[aria-label*='Search' i]"),
                new() { Timeout = 20000 });
            if (searchBox == null) throw new Exception("Search input not found on find-products page (authenticated, but the app shell didn't render the search)");

            await searchBox.FillAsync("");
            await searchBox.FillAsync(query);
            await searchBox.PressAsync("Enter");

            try
            {
                await session.Page.WaitForSelectorAsync(
                    SelectorList("[data-product-id]",
                                 "[data-testid='product-card']",
                                 ".product-card",
                                 "article"),
                    new() { Timeout = 15000, State = WaitForSelectorState.Attached });
            }
            catch (TimeoutException) { /* page may render differently; extract what's present */ }

            await session.Page.WaitForTimeoutAsync(1500);
            return await ExtractSearchResultsAsync(session.Page);
        }
        catch (SessionExpiredException) { throw; }
        catch (Exception ex)
        {
            await CaptureAsync(session, "search-failure", ex);
            throw;
        }
    }

    // ── add-to-import-list ──

    public async Task<string> AddToImportListAsync(
        ShopifyAutomationConfig config, string externalId, CancellationToken ct)
    {
        await using var session = await OpenSessionAsync(config, ct);
        try
        {
            var findUrl = config.FindProductsUrl;
            if (!session.Page.Url.StartsWith(findUrl, StringComparison.OrdinalIgnoreCase))
                await session.Page.GotoAsync(findUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

            await EnsureAuthenticatedAsync(session.Page, ct);

            var card = await session.Page.QuerySelectorAsync($"[data-product-id='{externalId}']")
                     ?? await session.Page.QuerySelectorAsync($"[data-external-id='{externalId}']")
                     ?? (await session.Page.QuerySelectorAllAsync("article,[data-testid='product-card'],.product-card")).FirstOrDefault();
            if (card == null)
                throw new Exception($"No product card found for externalId={externalId}");

            var addButton = await card.QuerySelectorAsync(
                SelectorList("button:has-text('Add to import list')",
                             "button:has-text('Import')",
                             "button[aria-label*='import' i]"));
            if (addButton == null)
                throw new Exception("'Add to import list' button not found on card");

            await addButton.ClickAsync(new() { Timeout = 15000 });

            try
            {
                await session.Page.WaitForSelectorAsync(
                    SelectorList(":has-text('Added to import list')",
                                 ":has-text('In import list')",
                                 "[data-import-status='added']"),
                    new() { Timeout = 10000 });
            }
            catch (TimeoutException) { /* no visible confirmation; assume the click registered */ }

            return externalId;
        }
        catch (SessionExpiredException) { throw; }
        catch (Exception ex)
        {
            await CaptureAsync(session, $"add-failure-{externalId}", ex);
            throw;
        }
    }

    // ── get-import-list ──

    public async Task<IReadOnlyList<AdminAppImportListItem>> GetImportListAsync(
        ShopifyAutomationConfig config, CancellationToken ct)
    {
        await using var session = await OpenSessionAsync(config, ct);
        try
        {
            var url = config.ImportListUrl;
            _logger.LogInformation("Navigating to {Url}", url);
            await session.Page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            await session.Page.WaitForTimeoutAsync(1200);

            await EnsureAuthenticatedAsync(session.Page, ct);

            try
            {
                await session.Page.WaitForSelectorAsync(
                    SelectorList("[data-import-item-id]",
                                 "[data-testid='import-list-row']",
                                 "table tbody tr",
                                 "article"),
                    new() { Timeout = 10000, State = WaitForSelectorState.Attached });
            }
            catch (TimeoutException) { /* empty list is valid */ }

            return await ExtractImportListAsync(session.Page);
        }
        catch (SessionExpiredException) { throw; }
        catch (Exception ex)
        {
            await CaptureAsync(session, "import-list-failure", ex);
            throw;
        }
    }

    // ── push-to-store ──

    public async Task<AdminAppPushResult> PushToStoreAsync(
        ShopifyAutomationConfig config, string importItemId, CancellationToken ct)
    {
        await using var session = await OpenSessionAsync(config, ct);
        try
        {
            var url = config.ImportListUrl;
            await session.Page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await EnsureAuthenticatedAsync(session.Page, ct);

            var row = await session.Page.QuerySelectorAsync($"[data-import-item-id='{importItemId}']")
                    ?? await session.Page.QuerySelectorAsync($"[data-external-id='{importItemId}']")
                    ?? (await session.Page.QuerySelectorAllAsync("[data-testid='import-list-row'],table tbody tr,article")).FirstOrDefault();
            if (row == null)
                return new AdminAppPushResult(false, null, $"Row not found for import item {importItemId}");

            var pushBtn = await row.QuerySelectorAsync(
                SelectorList("button:has-text('Push to store')",
                             "button:has-text('Push to Shopify')",
                             "button:has-text('Publish')",
                             "button[aria-label*='push' i]"));
            if (pushBtn == null)
                return new AdminAppPushResult(false, null, "'Push to store' button not found");

            await pushBtn.ClickAsync(new() { Timeout = 15000 });

            try
            {
                await session.Page.WaitForSelectorAsync(
                    SelectorList(":has-text('Pushed to store')",
                                 ":has-text('Published')",
                                 ":has-text('Success')"),
                    new() { Timeout = 60000 });
            }
            catch (TimeoutException)
            {
                return new AdminAppPushResult(false, null, "Timed out waiting for push confirmation");
            }

            return new AdminAppPushResult(true, null, null);
        }
        catch (SessionExpiredException) { throw; }
        catch (Exception ex)
        {
            await CaptureAsync(session, $"push-failure-{importItemId}", ex);
            return new AdminAppPushResult(false, null, ex.Message);
        }
    }

    // ── auth gate ──

    private async Task EnsureAuthenticatedAsync(IPage page, CancellationToken ct)
    {
        var diag = await LoginStateDetector.DetectAsync(page, ct);
        if (diag.State != LoginState.Authenticated && diag.State != LoginState.Unknown)
        {
            _logger.LogWarning("Auth gate tripped: state={State} url={Url} title={Title}",
                diag.State, diag.Url, diag.Title);
            await DumpDiagnosticsAsync(page, diag);
            throw new SessionExpiredException(diag);
        }

        // Persist any rotated cookies back to disk so subsequent steps use fresh cookies.
        try { await _sessions.SaveContextAsync(page.Context, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist rotated cookies after auth gate"); }
    }

    // ── browser plumbing ──

    private async Task<BrowserSession> OpenSessionAsync(ShopifyAutomationConfig config, CancellationToken ct)
    {
        await EnsureBrowserAsync(config, ct);
        var context = await _sessions.OpenAuthenticatedContextAsync(_browser!, ct);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(45000);
        return new BrowserSession(context, page);
    }

    private async Task EnsureBrowserAsync(ShopifyAutomationConfig config, CancellationToken ct)
    {
        if (_browser != null) return;
        await _browserLock.WaitAsync(ct);
        try
        {
            if (_browser != null) return;
            _pw ??= await Playwright.CreateAsync();
            _browser = await _pw.Chromium.LaunchAsync(new()
            {
                Headless = config.HeadlessMode,
                Args = new[] { "--disable-blink-features=AutomationControlled", "--no-sandbox" }
            });
            _logger.LogInformation("Playwright chromium launched (headless={Headless})", config.HeadlessMode);
        }
        finally { _browserLock.Release(); }
    }

    private async Task CaptureAsync(BrowserSession session, string label, Exception ex)
    {
        try
        {
            var file = Path.Combine(_screenshotDir, $"{label}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.png");
            await session.Page.ScreenshotAsync(new() { Path = file, FullPage = true });
            _logger.LogError(ex, "Automation step failed ({Label}) — screenshot: {File}", label, file);
        }
        catch (Exception capEx)
        {
            _logger.LogError(ex, "Automation step failed ({Label}) — screenshot capture also failed: {Msg}", label, capEx.Message);
        }
    }

    private async Task DumpDiagnosticsAsync(IPage page, LoginDiagnostics diag)
    {
        try
        {
            var file = Path.Combine(_screenshotDir,
                $"auth-gate-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.png");
            await page.ScreenshotAsync(new() { Path = file, FullPage = true });
            _logger.LogInformation("Auth-gate diagnostics: state={State} url={Url} title={Title} " +
                                   "emailField={Email} passwordField={Password} signInBtn={SignIn} " +
                                   "accountChooser={Chooser} insideAdmin={Admin} screenshot={File}",
                diag.State, diag.Url, diag.Title,
                diag.SawEmailField, diag.SawPasswordField, diag.SawSignInButton,
                diag.SawAccountChooser, diag.InsideAdminOrigin, file);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Auth diagnostics dump failed"); }
    }

    // ── extraction helpers ──

    private static async Task<IReadOnlyList<AdminAppSearchCandidate>> ExtractSearchResultsAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(@"() => {
          const cards = Array.from(document.querySelectorAll(
            '[data-product-id], [data-testid=""product-card""], article, .product-card'));
          const seen = new Set();
          const out = [];
          for (const c of cards) {
            const id = c.getAttribute('data-product-id') || c.getAttribute('data-external-id')
                    || c.id || crypto.randomUUID();
            if (seen.has(id)) continue;
            seen.add(id);
            const title = (c.querySelector('h1,h2,h3,[data-testid=""product-title""],.product-title')||{}).textContent
                       || (c.getAttribute('aria-label')||'').trim();
            const priceText = (c.querySelector('[data-testid=""price""],.price,[class*=""price"" i]')||{}).textContent||'';
            const img = (c.querySelector('img')||{}).src;
            const desc = (c.querySelector('[data-testid=""description""],.description,p')||{}).textContent;
            const vendor = (c.querySelector('[data-testid=""vendor""],.vendor')||{}).textContent;
            out.push({
              id, title: (title||'').trim(),
              price: parseFloat((priceText.match(/[\d\.]+/)||['0'])[0]),
              vendor: (vendor||'').trim() || null,
              img: img||null, desc: (desc||'').trim() || null
            });
          }
          return JSON.stringify(out);
        }");

        var list = new List<AdminAppSearchCandidate>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        using var doc = JsonDocument.Parse(json);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var id = el.GetProperty("id").GetString() ?? Guid.NewGuid().ToString();
            var title = el.GetProperty("title").GetString() ?? "(untitled)";
            decimal price = 0m;
            if (el.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var pd)) price = pd;
            var vendor = el.TryGetProperty("vendor", out var v) ? v.GetString() : null;
            var img = el.TryGetProperty("img", out var i) ? i.GetString() : null;
            var desc = el.TryGetProperty("desc", out var d) ? d.GetString() : null;
            if (string.IsNullOrWhiteSpace(title)) continue;
            list.Add(new AdminAppSearchCandidate(id, title, price, vendor, img, desc));
        }
        return list;
    }

    private static async Task<IReadOnlyList<AdminAppImportListItem>> ExtractImportListAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(@"() => {
          const rows = Array.from(document.querySelectorAll(
            '[data-import-item-id], [data-testid=""import-list-row""], table tbody tr, article'));
          const out = [];
          for (const r of rows) {
            const importId = r.getAttribute('data-import-item-id') || r.getAttribute('data-id') || r.id;
            const external = r.getAttribute('data-external-id') || r.getAttribute('data-product-id') || importId;
            const title = (r.querySelector('h1,h2,h3,[data-testid=""title""],.product-title,td:first-child')||{}).textContent
                       || (r.getAttribute('aria-label')||'').trim();
            if (!importId && !title) continue;
            out.push({
              importId: importId || crypto.randomUUID(),
              external: external || importId,
              title: (title||'').trim() || '(untitled)'
            });
          }
          return JSON.stringify(out);
        }");

        var list = new List<AdminAppImportListItem>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        using var doc = JsonDocument.Parse(json);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var importId = el.GetProperty("importId").GetString() ?? Guid.NewGuid().ToString();
            var external = el.GetProperty("external").GetString() ?? importId;
            var title = el.GetProperty("title").GetString() ?? "(untitled)";
            list.Add(new AdminAppImportListItem(importId, external, title));
        }
        return list;
    }

    private static string SelectorList(params string[] selectors) => string.Join(", ", selectors);

    public async ValueTask DisposeAsync()
    {
        if (_browser != null) { await _browser.DisposeAsync(); _browser = null; }
        _pw?.Dispose(); _pw = null;
        _browserLock.Dispose();
    }

    private sealed class BrowserSession : IAsyncDisposable
    {
        public IBrowserContext Context { get; }
        public IPage Page { get; }
        public BrowserSession(IBrowserContext ctx, IPage page) { Context = ctx; Page = page; }
        public async ValueTask DisposeAsync() => await Context.DisposeAsync();
    }
}
