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
            // Mimic the user flow: go to the APP ROOT first (equivalent to clicking
            // "Dropshipper.ai" from the Apps sidebar), let Shopify bootstrap the app-bridge
            // context, THEN navigate to Find Products inside the iframe. Deep-linking
            // straight to /app/find-products skips Shopify's session-token hydration.
            var appFrame = await OpenAppThenNavigateAsync(session.Page, config, "find-products", ct);
            _logger.LogInformation("Operating on frame: {FrameUrl}", appFrame.Url);

            // Find the search input within that frame (with a single, bounded wait).
            var searchSelector = SelectorList(
                "input[type='search']",
                "input[placeholder*='Search' i]",
                "[role='searchbox']",
                "input[aria-label*='Search' i]");

            IElementHandle? searchBox = null;
            try
            {
                searchBox = await appFrame.WaitForSelectorAsync(searchSelector,
                    new() { Timeout = 20000, State = WaitForSelectorState.Visible });
            }
            catch (TimeoutException)
            {
                await DumpFrameTreeAsync(session.Page, "search-not-found");
                await CaptureAsync(session, "search-not-found", new Exception("Search input not found"));
                throw new Exception(
                    "Search input not found in app frame after 20s. " +
                    "Check the screenshot at /tmp/autocommerce-automation/ — " +
                    "the iframe may be using a different selector, or the app is stuck loading.");
            }

            await searchBox!.FillAsync("");
            await searchBox.FillAsync(query);
            await searchBox.PressAsync("Enter");

            try
            {
                await appFrame.WaitForSelectorAsync(
                    SelectorList("[data-product-id]",
                                 "[data-testid='product-card']",
                                 ".product-card",
                                 "article"),
                    new() { Timeout = 15000, State = WaitForSelectorState.Attached });
            }
            catch (TimeoutException) { /* page may render differently; extract what's present */ }

            await session.Page.WaitForTimeoutAsync(1500);
            return await ExtractSearchResultsAsync(appFrame);
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
            var appFrame = await OpenAppThenNavigateAsync(session.Page, config, "find-products", ct);

            var card = await appFrame.QuerySelectorAsync($"[data-product-id='{externalId}']")
                     ?? await appFrame.QuerySelectorAsync($"[data-external-id='{externalId}']")
                     ?? (await appFrame.QuerySelectorAllAsync("article,[data-testid='product-card'],.product-card")).FirstOrDefault();
            if (card == null)
                throw new Exception($"No product card found for externalId={externalId}");

            var addButton = await card.QuerySelectorAsync(
                SelectorList("button:has-text('Add to import list')",
                             "button:has-text('Import')",
                             "button[aria-label*='import' i]"));
            if (addButton == null)
                throw new Exception("'Add to import list' button not found on card");

            await addButton.ScrollIntoViewIfNeededAsync();
            await addButton.ClickAsync(new() { Timeout = 15000 });

            try
            {
                await appFrame.WaitForSelectorAsync(
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
            var appFrame = await OpenAppThenNavigateAsync(session.Page, config, "import-list", ct);

            try
            {
                await appFrame.WaitForSelectorAsync(
                    SelectorList("[data-import-item-id]",
                                 "[data-testid='import-list-row']",
                                 "table tbody tr",
                                 "article"),
                    new() { Timeout = 10000, State = WaitForSelectorState.Attached });
            }
            catch (TimeoutException) { /* empty list is valid */ }

            return await ExtractImportListAsync(appFrame);
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
            var appFrame = await OpenAppThenNavigateAsync(session.Page, config, "import-list", ct);

            var row = await appFrame.QuerySelectorAsync($"[data-import-item-id='{importItemId}']")
                    ?? await appFrame.QuerySelectorAsync($"[data-external-id='{importItemId}']")
                    ?? (await appFrame.QuerySelectorAllAsync("[data-testid='import-list-row'],table tbody tr,article")).FirstOrDefault();
            if (row == null)
                return new AdminAppPushResult(false, null, $"Row not found for import item {importItemId}");

            var pushBtn = await row.QuerySelectorAsync(
                SelectorList("button:has-text('Push to store')",
                             "button:has-text('Push to Shopify')",
                             "button:has-text('Publish')",
                             "button[aria-label*='push' i]"));
            if (pushBtn == null)
                return new AdminAppPushResult(false, null, "'Push to store' button not found");

            await pushBtn.ScrollIntoViewIfNeededAsync();
            await pushBtn.ClickAsync(new() { Timeout = 15000 });

            try
            {
                await appFrame.WaitForSelectorAsync(
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

    /// <summary>
    /// Picks the "correct" target URL for the page's current location — so that when
    /// we're on the import-list page, we check against <c>config.ImportListUrl</c>, and
    /// when we're on find-products we check against <c>config.FindProductsUrl</c>.
    /// This prevents the auth gate from firing false positives right after navigation.
    /// </summary>
    private static string CurrentTargetFor(ShopifyAutomationConfig config, string pageUrl)
    {
        var u = (pageUrl ?? "").ToLowerInvariant();
        if (!string.IsNullOrEmpty(config.ImportListUrl) &&
            u.StartsWith(config.ImportListUrl.ToLowerInvariant()))
            return config.ImportListUrl;
        return config.FindProductsUrl;
    }

    private async Task EnsureAuthenticatedAsync(IPage page, string? targetUrl, CancellationToken ct)
    {
        var diag = await LoginStateDetector.DetectAsync(page, targetUrl, ct);
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
        // Prefer reusing the manual-login browser (raw Chrome, no automation flags)
        var manualPage = await _sessions.GetManualLoginPageAsync(ct);
        if (manualPage != null)
        {
            _logger.LogInformation("Reusing manual-login Chrome browser for automation");
            // Create a NEW page in the same context so we don't interfere with the login page
            var newPage = await manualPage.Context.NewPageAsync();
            newPage.SetDefaultTimeout(45000);
            // ownsContext=false so we don't close the whole Chrome context on dispose
            // but we do close this specific page
            return new BrowserSession(manualPage.Context, newPage, ownsContext: false);
        }

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

    // ── navigation helpers ──

    /// <summary>
    /// Mimics the manual user flow: navigates to the app root (equivalent to clicking
    /// "Dropshipper.ai" from the Shopify Apps sidebar), waits for the embedded iframe
    /// to bootstrap, then navigates inside the iframe to the requested sub-page
    /// ("find-products" or "import-list"). Returns the authenticated app frame.
    ///
    /// Going through the root is important because the app-bridge session token
    /// hydration only happens when Shopify admin loads the app as an embedded
    /// context — deep-linking straight to /app/find-products can skip it.
    /// </summary>
    private async Task<IFrame> OpenAppThenNavigateAsync(
        IPage page, ShopifyAutomationConfig config, string subPage, CancellationToken ct)
    {
        // Derive the app root: strip /app/<anything> off the Find Products URL.
        var findUrl = config.FindProductsUrl ?? "";
        var appRoot = findUrl;
        var appIdx = findUrl.IndexOf("/app/", StringComparison.OrdinalIgnoreCase);
        if (appIdx > 0) appRoot = findUrl[..appIdx];

        var targetSubUrl = subPage switch
        {
            "find-products" => config.FindProductsUrl,
            "import-list" => config.ImportListUrl,
            _ => findUrl,
        };

        // Only navigate to the app root if we're not already inside the app.
        var currentUrl = page.Url ?? "";
        if (!currentUrl.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Opening app root: {Url}", appRoot);
            await page.GotoAsync(appRoot, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
            // Skip NetworkIdle — Shopify admin has analytics beacons that never quiesce.
            // Short fixed settle is faster and reliable enough for the iframe to mount.
            await page.WaitForTimeoutAsync(2500);

            await EnsureAuthenticatedAsync(page, appRoot, ct);

            // Give the admin shell a beat to mount the iframe.
            await page.WaitForTimeoutAsync(1500);
        }

        // Get the app iframe (mounted now that the shell has loaded).
        var appFrame = await WaitForAppFrameAsync(page, config.AppUrl, ct);

        // Navigate inside the iframe to the sub-page. Two strategies:
        // (a) click an in-app link if one exists (mimics user interaction);
        // (b) if the app uses client-side routing, we can set the iframe's src directly.
        // We try (a) first for a more natural interaction; fall back to direct navigation.
        var subLinkClicked = await TryClickInAppLinkAsync(appFrame, subPage);
        if (!subLinkClicked && !string.IsNullOrWhiteSpace(targetSubUrl))
        {
            _logger.LogInformation("In-app link not found, navigating page directly to {Url}", targetSubUrl);
            await page.GotoAsync(targetSubUrl!, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
            await page.WaitForTimeoutAsync(2500);
            await EnsureAuthenticatedAsync(page, targetSubUrl, ct);
            appFrame = await WaitForAppFrameAsync(page, config.AppUrl, ct);
        }
        else if (subLinkClicked)
        {
            _logger.LogInformation("Clicked in-app link for {SubPage}; waiting for content", subPage);
            await page.WaitForTimeoutAsync(2000);
        }

        return appFrame;
    }

    /// <summary>
    /// Looks for and clicks a navigation link inside the app frame that takes us to the
    /// requested sub-page (Find Products / Import List). Returns true if it clicked
    /// something; false if no matching link was found.
    /// </summary>
    private async Task<bool> TryClickInAppLinkAsync(IFrame appFrame, string subPage)
    {
        var needles = subPage switch
        {
            "find-products" => new[] { "Find products", "Find Product", "find-products" },
            "import-list" => new[] { "Import list", "Import List", "import-list" },
            _ => Array.Empty<string>(),
        };

        foreach (var needle in needles)
        {
            try
            {
                var link = await appFrame.QuerySelectorAsync(
                    $"a:has-text('{needle}'), button:has-text('{needle}'), [href*='{needle}' i]");
                if (link != null)
                {
                    _logger.LogInformation("Found in-app nav link matching '{Needle}'", needle);
                    await link.ScrollIntoViewIfNeededAsync();
                    try { await link.ClickAsync(new() { Timeout = 5000, Force = true }); }
                    catch { await link.EvaluateAsync("el => el.click()"); }
                    return true;
                }
            }
            catch { /* try next selector */ }
        }
        return false;
    }

    /// <summary>
    /// Polls for the iframe where the dropshipper-ai UI actually renders. Shopify admin
    /// nests the app inside <c>&lt;iframe src=&quot;https://app.dropshiping.ai/...&quot;&gt;</c>
    /// and only mounts it after the admin shell finishes loading — we can't assume it's
    /// present at navigation time.
    /// </summary>
    private async Task<IFrame> WaitForAppFrameAsync(IPage page, string? appUrlHint, CancellationToken ct)
    {
        var hints = new List<string> { "dropshiping.ai", "dropshipper-ai" };
        if (!string.IsNullOrWhiteSpace(appUrlHint))
        {
            try { hints.Insert(0, new Uri(appUrlHint).Host); } catch { /* invalid URL; hints list already has fallbacks */ }
        }

        var deadline = DateTime.UtcNow.AddSeconds(25);
        IFrame? best = null;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var f in page.Frames)
            {
                if (f == page.MainFrame) continue;
                var url = f.Url ?? "";
                if (hints.Any(h => url.Contains(h, StringComparison.OrdinalIgnoreCase)))
                {
                    // Found the app iframe — wait for it to actually finish loading.
                    try { await f.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 10000 }); }
                    catch (TimeoutException) { /* continue anyway */ }
                    _logger.LogInformation("App frame detected: {Url}", url);
                    return f;
                }
                // Track any non-main frame as a fallback.
                if (best == null) best = f;
            }
            await Task.Delay(500, ct);
        }

        if (best != null)
        {
            _logger.LogWarning("No frame matched app hints {Hints}; falling back to first subframe {Url}",
                string.Join(",", hints), best.Url);
            return best;
        }

        // No iframes at all — operate on the main frame. This happens when the app
        // renders top-level (not embedded).
        _logger.LogInformation("No subframes found — using main frame");
        return page.MainFrame;
    }

    private async Task DumpFrameTreeAsync(IPage page, string label)
    {
        try
        {
            var lines = new List<string> { $"[{label}] top={page.Url} title={await page.TitleAsync()}" };
            foreach (var f in page.Frames)
            {
                var kind = f == page.MainFrame ? "main" : "frame";
                lines.Add($"  {kind}: {f.Url} (name={f.Name})");
            }
            _logger.LogWarning("Frame tree:\n{Tree}", string.Join("\n", lines));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "DumpFrameTreeAsync failed"); }
    }

    // ── extraction helpers ──

    private static async Task<IReadOnlyList<AdminAppSearchCandidate>> ExtractSearchResultsAsync(IFrame frame)
    {
        var json = await frame.EvaluateAsync<string>(@"() => {
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

    private static async Task<IReadOnlyList<AdminAppImportListItem>> ExtractImportListAsync(IFrame frame)
    {
        var json = await frame.EvaluateAsync<string>(@"() => {
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
        private readonly bool _ownsContext;
        public BrowserSession(IBrowserContext ctx, IPage page, bool ownsContext = true)
        {
            Context = ctx; Page = page; _ownsContext = ownsContext;
        }
        public async ValueTask DisposeAsync()
        {
            if (_ownsContext)
                await Context.DisposeAsync();
            else
                try { await Page.CloseAsync(); } catch { /* page may already be closed */ }
        }
    }
}
