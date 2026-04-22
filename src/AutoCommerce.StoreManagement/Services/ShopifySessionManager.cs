using System.Text.Json;
using System.Diagnostics;
using Microsoft.Playwright;
using AutoCommerce.StoreManagement.Domain;

namespace AutoCommerce.StoreManagement.Services;

public enum ShopifySessionState
{
    Unknown,          // never validated / no state file present
    Connected,        // last validation saw the app authenticated
    LoginRequired,    // last validation saw login page / account picker
    Error,            // validation itself threw
}

public record ShopifySessionStatusDto(
    string State,                       // unknown | connected | login_required | error
    DateTimeOffset? LastValidatedAt,
    DateTimeOffset? LastLoggedInAt,
    bool StorageStateExists,
    string? Message);

public interface IShopifySessionManager
{
    /// <summary>Reads the last-known status from disk — cheap, no browser work.</summary>
    Task<ShopifySessionStatusDto> GetStatusAsync(CancellationToken ct);

    /// <summary>Opens a headless browser with the saved storage state and navigates to
    /// <c>config.FindProductsUrl</c>. Classifies the resulting page and updates the status.
    /// This is the canonical "is my session still good?" check.</summary>
    Task<ShopifySessionStatusDto> ValidateAsync(ShopifyAutomationConfig config, CancellationToken ct);

    /// <summary>Launches a NON-headless browser context pointed at <c>config.FindProductsUrl</c>,
    /// waits for the user to finish logging in, then writes the resulting Playwright
    /// storage state to disk. Requires a display (X11/VNC) — in headless environments
    /// this will surface an error, and the UI should fall back to
    /// <see cref="ImportStorageStateAsync"/>.</summary>
    Task<ShopifySessionStatusDto> StartInteractiveLoginAsync(ShopifyAutomationConfig config, CancellationToken ct);

    /// <summary>Launches a headless browser, navigates to Shopify login, fills email/password.
    /// If verification is needed, returns verification_required state and keeps browser alive.</summary>
    Task<ShopifySessionStatusDto> LoginWithCredentialsAsync(string email, string password, ShopifyAutomationConfig config, CancellationToken ct);

    /// <summary>Submits a verification code (2FA / email code) to the pending login session.</summary>
    Task<ShopifySessionStatusDto> SubmitVerificationCodeAsync(string code, ShopifyAutomationConfig config, CancellationToken ct);

    /// <summary>Launches a headed browser visible via noVNC at port 6080.
    /// User logs in manually as a human; the system polls and captures the session.</summary>
    Task<ShopifySessionStatusDto> StartManualBrowserLoginAsync(ShopifyAutomationConfig config, CancellationToken ct);

    /// <summary>Checks the status of a manual browser login session (is the user authenticated yet?).</summary>
    Task<ShopifySessionStatusDto> PollManualLoginAsync(ShopifyAutomationConfig config, CancellationToken ct);

    /// <summary>Stops the manual browser login session and kills VNC.</summary>
    Task StopManualLoginAsync();

    /// <summary>Accepts a Playwright storage-state JSON (same shape as the output of
    /// <c>context.StorageStateAsync()</c>) and writes it to disk. This is the
    /// headless-docker-friendly way to hand the automation an authenticated session.</summary>
    Task<ShopifySessionStatusDto> ImportStorageStateAsync(string storageStateJson, CancellationToken ct);

    /// <summary>Opens a fresh browser context pre-loaded with the saved storage state.
    /// Callers own the returned context and must dispose it.</summary>
    Task<IBrowserContext> OpenAuthenticatedContextAsync(IBrowser browser, CancellationToken ct);

    /// <summary>Persists any rotated cookies from the context back to the storage-state
    /// file so subsequent context creations pick them up.</summary>
    Task SaveContextAsync(IBrowserContext context, CancellationToken ct);

    /// <summary>If a manual login browser is still alive, returns its page for automation reuse.
    /// Returns null if no manual session is active.</summary>
    Task<IPage?> GetManualLoginPageAsync(CancellationToken ct);

    /// <summary>Absolute path to the storage-state file on disk (used for diagnostics).</summary>
    string StorageStatePath { get; }
}

public class ShopifySessionManager : IShopifySessionManager, IAsyncDisposable
{
    internal const string ConsistentUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36";

    private readonly ILogger<ShopifySessionManager> _logger;
    private readonly string _stateDir;
    private readonly string _stateFile;
    private readonly string _metaFile;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private IPlaywright? _pw;
    private IBrowser? _headlessBrowser;
    private IBrowser? _headedBrowser;

    // Pending credential login — kept alive for verification code entry
    private IBrowserContext? _pendingLoginContext;
    private IPage? _pendingLoginPage;
    private string? _pendingLoginTargetUrl;
    private DateTimeOffset _pendingLoginExpiry;

    // Manual browser login — raw Chrome visible via noVNC, connected via CDP
    private IBrowserContext? _manualLoginContext;
    private IPage? _manualLoginPage;
    private string? _manualLoginTargetUrl;
    private DateTimeOffset _manualLoginExpiry;
    private Process? _vncProcess;
    private Process? _websockifyProcess;
    private Process? _chromeProcess;
    private IBrowser? _cdpBrowser;

    public ShopifySessionManager(ILogger<ShopifySessionManager> logger, IConfiguration config)
    {
        _logger = logger;
        _stateDir = config["Automation:StateDir"] ?? "/app/data";
        Directory.CreateDirectory(_stateDir);
        _stateFile = Path.Combine(_stateDir, "shopify-session.json");
        _metaFile = Path.Combine(_stateDir, "shopify-session.meta.json");
    }

    public string StorageStatePath => _stateFile;

    public Task<ShopifySessionStatusDto> GetStatusAsync(CancellationToken ct) =>
        Task.FromResult(ReadStatus());

    public async Task<ShopifySessionStatusDto> ValidateAsync(
        ShopifyAutomationConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.FindProductsUrl))
            return WriteStatus(ShopifySessionState.Error, "Find Products URL is not configured");

        if (!File.Exists(_stateFile))
            return WriteStatus(ShopifySessionState.LoginRequired, "No saved session — connect Shopify first");

        await _mutex.WaitAsync(ct);
        try
        {
            var browser = await GetHeadlessBrowserAsync(ct);
            await using var context = await browser.NewContextAsync(new()
            {
                StorageStatePath = _stateFile,
                ViewportSize = new() { Width = 1280, Height = 800 },
                UserAgent = ConsistentUserAgent,
            });
            var page = await context.NewPageAsync();
            try
            {
                await page.GotoAsync(config.FindProductsUrl, new()
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30000,
                });
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("net::ERR"))
            {
                return WriteStatus(ShopifySessionState.Error, $"Navigation failed: {ex.Message}");
            }

            await page.WaitForTimeoutAsync(1500);
            var diag = await LoginStateDetector.DetectAsync(page, config.FindProductsUrl, ct);
            _logger.LogInformation("Validate: landed on {Url} → {State} ({Notes})",
                diag.Url, diag.State, diag.Notes ?? "-");

            // Persist any rotated cookies back to disk so automation picks them up.
            if (diag.State == LoginState.Authenticated)
            {
                try { await context.StorageStateAsync(new() { Path = _stateFile }); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist rotated cookies after validation"); }
            }

            var cookieHint = " Tip: paste the full Playwright storageState() output, or export ALL cookies from admin.shopify.com (including _secure_admin_session_id_* / _master_udr / shopify_user_t) — the three generic shopify.com cookies on their own are not enough.";

            return diag.State switch
            {
                LoginState.Authenticated => WriteStatus(ShopifySessionState.Connected, $"Authenticated on {diag.Url}"),
                LoginState.LoginPage => WriteStatus(ShopifySessionState.LoginRequired,
                    $"Redirected to login ({diag.Url}) — session is not authenticated.{cookieHint}"),
                LoginState.AccountSelection => WriteStatus(ShopifySessionState.LoginRequired,
                    $"Got the Shopify store picker ({diag.Url}) — cookies exist but aren't scoped to the target store.{cookieHint}"),
                LoginState.NotInApp => WriteStatus(ShopifySessionState.LoginRequired,
                    $"Landed on {diag.Url} but not on the app path — session may be scoped to a different store.{cookieHint}"),
                _ => WriteStatus(ShopifySessionState.Unknown, diag.Notes ?? "Could not classify page"),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ValidateAsync failed");
            return WriteStatus(ShopifySessionState.Error, ex.Message);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ShopifySessionStatusDto> StartInteractiveLoginAsync(
        ShopifyAutomationConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.FindProductsUrl))
            return WriteStatus(ShopifySessionState.Error, "Find Products URL is not configured");

        await _mutex.WaitAsync(ct);
        try
        {
            // Always headed — the whole point is for the user to see the login form.
            _pw ??= await Playwright.CreateAsync();
            _headedBrowser ??= await _pw.Chromium.LaunchAsync(new()
            {
                Headless = false,
                Args = new[] { "--disable-blink-features=AutomationControlled", "--no-sandbox" }
            });

            var initialStatePath = File.Exists(_stateFile) ? _stateFile : null;

            await using var context = await _headedBrowser.NewContextAsync(new()
            {
                StorageStatePath = initialStatePath,
                ViewportSize = new() { Width = 1440, Height = 900 },
            });
            var page = await context.NewPageAsync();
            await page.GotoAsync(config.FindProductsUrl, new() { Timeout = 45000 });

            // Poll for up to 5 minutes for the user to finish logging in.
            var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
            LoginDiagnostics? lastDiag = null;
            while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                try
                {
                    lastDiag = await LoginStateDetector.DetectAsync(page, config.FindProductsUrl, ct);
                    if (lastDiag.State == LoginState.Authenticated) break;
                }
                catch { /* page may be mid-navigation */ }
                await Task.Delay(2000, ct);
            }

            if (lastDiag?.State != LoginState.Authenticated)
                return WriteStatus(ShopifySessionState.LoginRequired,
                    "Timed out waiting for login — re-try when ready");

            await context.StorageStateAsync(new() { Path = _stateFile });
            WriteMeta(DateTimeOffset.UtcNow);
            return WriteStatus(ShopifySessionState.Connected, "Session saved after interactive login");
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("BROWSER") || ex.Message.Contains("xdo") || ex.Message.Contains("DISPLAY"))
        {
            // Most likely: headed browser cannot open because there's no display.
            return WriteStatus(ShopifySessionState.Error,
                "Headed browser could not launch (no display available). " +
                "Use 'Upload Session' instead — paste Playwright storage-state JSON from a browser where you're already logged in.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartInteractiveLoginAsync failed");
            return WriteStatus(ShopifySessionState.Error, ex.Message);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ShopifySessionStatusDto> LoginWithCredentialsAsync(
        string email, string password, ShopifyAutomationConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return WriteStatus(ShopifySessionState.Error, "Email and password are required");

        // Clean up any previous pending login
        await DisposePendingLoginAsync();

        var targetUrl = config.FindProductsUrl;
        if (string.IsNullOrWhiteSpace(targetUrl))
            targetUrl = config.ShopifyStoreUrl;
        if (string.IsNullOrWhiteSpace(targetUrl))
            targetUrl = "https://admin.shopify.com";

        await _mutex.WaitAsync(ct);
        try
        {
            // Use a HEADED browser with a virtual display (Xvfb) — much harder to detect
            var browser = await GetHeadedLoginBrowserAsync(ct);
            var context = await browser.NewContextAsync(new()
            {
                ViewportSize = new() { Width = 1440, Height = 900 },
                UserAgent = ConsistentUserAgent,
                Locale = "en-US",
                TimezoneId = "America/New_York",
                HasTouch = false,
                JavaScriptEnabled = true,
                DeviceScaleFactor = 1,
            });
            var page = await context.NewPageAsync();
            await InjectStealthAsync(page);

            var rng = new Random();
            await page.WaitForTimeoutAsync(rng.Next(500, 1500));

            _logger.LogInformation("Credential login (headed): navigating to {Url}", targetUrl);
            // NetworkIdle never settles on Shopify admin (analytics beacons run forever),
            // so the old GotoAsync was hanging the full 30s timeout. DOMContentLoaded +
            // a short settle delay is what we actually need.
            await page.GotoAsync(targetUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
            await page.WaitForTimeoutAsync(rng.Next(1500, 3000));

            // Detect Cloudflare / anti-bot interstitials so we don't wait on them forever.
            await FailIfCloudflareAsync(page);

            // ── Fill email ──
            var emailSelector = "input[type='email'], input[name='account[email]'], input[autocomplete='username'], #account_email";
            var emailInput = await page.QuerySelectorAsync(emailSelector);
            if (emailInput == null)
            {
                await page.GotoAsync("https://accounts.shopify.com/lookup", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
                await page.WaitForTimeoutAsync(rng.Next(1500, 3000));
                await FailIfCloudflareAsync(page);
                emailInput = await page.QuerySelectorAsync(emailSelector);
            }

            if (emailInput == null)
            {
                await context.DisposeAsync();
                return WriteStatus(ShopifySessionState.Error,
                    $"Could not find email input on {page.Url}. The login page structure may have changed.");
            }

            await HumanTypeAsync(page, emailInput, email);
            _logger.LogInformation("Credential login: filled email");
            await page.WaitForTimeoutAsync(rng.Next(400, 1000));

            var continueBtn = await page.QuerySelectorAsync(
                "button[type='submit'], button:has-text('Continue'), button:has-text('Next'), button:has-text('Log in')");
            if (continueBtn != null)
            {
                await MoveMouseToElementAsync(page, continueBtn);
                await page.WaitForTimeoutAsync(rng.Next(100, 300));
                await SafeClickAsync(continueBtn);
            }
            else
                await page.Keyboard.PressAsync("Enter");

            await page.WaitForTimeoutAsync(rng.Next(3000, 5000));

            // ── Fill password ──
            var passwordInput = await page.QuerySelectorAsync("input[type='password']");
            if (passwordInput == null)
            {
                try { await page.WaitForSelectorAsync("input[type='password']", new() { Timeout = 10000 }); }
                catch { /* timeout */ }
                passwordInput = await page.QuerySelectorAsync("input[type='password']");
            }

            if (passwordInput == null)
            {
                await context.DisposeAsync();
                return WriteStatus(ShopifySessionState.Error,
                    $"Could not find password field on {page.Url}. Shopify may require a different auth flow (e.g. magic link).");
            }

            await HumanTypeAsync(page, passwordInput, password);
            _logger.LogInformation("Credential login: filled password");
            await page.WaitForTimeoutAsync(rng.Next(300, 800));

            var loginBtn = await page.QuerySelectorAsync(
                "button[type='submit'], button:has-text('Log in'), button:has-text('Sign in')");
            if (loginBtn != null)
            {
                await MoveMouseToElementAsync(page, loginBtn);
                await page.WaitForTimeoutAsync(rng.Next(100, 300));
                await SafeClickAsync(loginBtn);
            }
            else
                await page.Keyboard.PressAsync("Enter");

            // ── Short wait to see if we land directly on auth or hit a verification page ──
            _logger.LogInformation("Credential login: waiting after password submit...");
            await page.WaitForTimeoutAsync(5000);

            // Take a screenshot for debugging
            try
            {
                var screenshotDir = Path.Combine(_stateDir, "debug");
                Directory.CreateDirectory(screenshotDir);
                var screenshotPath = Path.Combine(screenshotDir, $"login-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.png");
                await page.ScreenshotAsync(new() { Path = screenshotPath, FullPage = true });
                _logger.LogInformation("Credential login: screenshot saved to {Path}", screenshotPath);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not save screenshot"); }

            _logger.LogInformation("Credential login: post-submit URL = {Url}", page.Url);

            // Check for login errors first
            var errorEl = await page.QuerySelectorAsync(".error-message, [data-error], .banner--critical, .Polaris-Banner--critical, .notice--error, .Polaris-InlineError");
            if (errorEl != null)
            {
                var errorText = await errorEl.InnerTextAsync();
                if (!string.IsNullOrWhiteSpace(errorText))
                {
                    await context.DisposeAsync();
                    return WriteStatus(ShopifySessionState.Error, $"Login failed: {errorText.Trim()}");
                }
            }

            // ── Wait up to 30s for the page to resolve (the verify challenge may auto-redirect) ──
            var postLoginDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
            LoginDiagnostics? postDiag = null;
            while (DateTimeOffset.UtcNow < postLoginDeadline && !ct.IsCancellationRequested)
            {
                postDiag = await LoginStateDetector.DetectAsync(page, config.FindProductsUrl, ct);
                _logger.LogInformation("Credential login: page state = {State}, url = {Url}", postDiag.State, postDiag.Url);
                if (postDiag.State == LoginState.Authenticated) break;
                
                // If URL changed away from the verify page, it might be progressing
                var nowUrl = page.Url ?? "";
                if (postDiag.State == LoginState.AccountSelection)
                {
                    // We're past auth but need to pick a store — navigate to target
                    _logger.LogInformation("Credential login: store picker detected, navigating to target");
                    await page.GotoAsync(targetUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
                    await page.WaitForTimeoutAsync(3000);
                    postDiag = await LoginStateDetector.DetectAsync(page, config.FindProductsUrl, ct);
                    if (postDiag.State == LoginState.Authenticated) break;
                }

                await page.WaitForTimeoutAsync(2000);
            }

            if (postDiag?.State == LoginState.Authenticated)
            {
                await context.StorageStateAsync(new() { Path = _stateFile });
                WriteMeta(DateTimeOffset.UtcNow);
                await context.DisposeAsync();
                _logger.LogInformation("Credential login: SUCCESS!");
                return WriteStatus(ShopifySessionState.Connected, "Session saved after credential login");
            }

            // Take another screenshot after waiting
            try
            {
                var screenshotDir = Path.Combine(_stateDir, "debug");
                var screenshotPath = Path.Combine(screenshotDir, $"login-final-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.png");
                await page.ScreenshotAsync(new() { Path = screenshotPath, FullPage = true });
                _logger.LogInformation("Credential login: final screenshot saved to {Path}", screenshotPath);
            }
            catch { }

            // ── Detect verification/2FA page ──
            // Only trust actual code input fields on the page, NOT the URL "verify" param
            // (accounts.shopify.com/lookup?verify=... is an anti-bot challenge, not a code prompt).
            var currentUrl = page.Url ?? "";
            var isVerifyPath = currentUrl.Contains("/two_factor", StringComparison.OrdinalIgnoreCase)
                || currentUrl.Contains("/challenge", StringComparison.OrdinalIgnoreCase)
                || currentUrl.Contains("/otp", StringComparison.OrdinalIgnoreCase)
                || currentUrl.Contains("/verify_code", StringComparison.OrdinalIgnoreCase);

            // Check for actual code input fields on the page
            var codeInput = await page.QuerySelectorAsync(
                "input[autocomplete='one-time-code'], input[name='code'], input[name='otp'], " +
                "input[data-ui='verify-code-input']");
            // Also check for multiple single-digit inputs (common 2FA pattern)
            var digitInputs = codeInput == null
                ? await page.QuerySelectorAllAsync("input[maxlength='1'][inputmode='numeric'], input[maxlength='1'][type='tel']")
                : new List<IElementHandle>();
            var hasCodeInputs = codeInput != null || digitInputs.Count >= 4;

            if (isVerifyPath || hasCodeInputs)
            {
                _logger.LogInformation("Credential login: verification/2FA page detected at {Url}. Keeping browser alive.", currentUrl);
                // Keep context alive for verification code submission
                _pendingLoginContext = context;
                _pendingLoginPage = page;
                _pendingLoginTargetUrl = targetUrl;
                _pendingLoginExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
                return new ShopifySessionStatusDto(
                    State: "verification_required",
                    LastValidatedAt: DateTimeOffset.UtcNow,
                    LastLoggedInAt: null,
                    StorageStateExists: File.Exists(_stateFile),
                    Message: $"Shopify is asking for a verification code (check your email/phone). Enter the code to continue. Page: {currentUrl}");
            }

            // Not verified, not authenticated — check if Shopify anti-bot blocked us
            var isAntiBot = currentUrl.Contains("accounts.shopify.com/lookup", StringComparison.OrdinalIgnoreCase)
                && currentUrl.Contains("verify=", StringComparison.OrdinalIgnoreCase);

            // Try to grab page text for more context
            string pageHint = "";
            try
            {
                var bodyText = await page.InnerTextAsync("body");
                if (bodyText.Length > 200) bodyText = bodyText[..200];
                pageHint = $" Page text: {bodyText.Trim()}";
            }
            catch { /* ignore */ }

            await context.DisposeAsync();

            if (isAntiBot)
                return WriteStatus(ShopifySessionState.LoginRequired,
                    "Shopify detected automated login and blocked it with an anti-bot challenge. " +
                    "This means credentials were likely correct, but Shopify won't allow headless browser login. " +
                    "Use the Cookie-Editor extension method or the capture-shopify-session.mjs tool instead.");

            return WriteStatus(ShopifySessionState.LoginRequired,
                $"Login did not complete — ended on {currentUrl}. Wrong credentials or Shopify blocked the login.{pageHint}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoginWithCredentialsAsync failed");
            await DisposePendingLoginAsync();
            return WriteStatus(ShopifySessionState.Error, $"Credential login error: {ex.Message}");
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ShopifySessionStatusDto> SubmitVerificationCodeAsync(
        string code, ShopifyAutomationConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
            return WriteStatus(ShopifySessionState.Error, "Verification code is required");

        if (_pendingLoginPage == null || _pendingLoginContext == null)
            return WriteStatus(ShopifySessionState.Error,
                "No pending login session. Start a new login with email & password first.");

        if (DateTimeOffset.UtcNow > _pendingLoginExpiry)
        {
            await DisposePendingLoginAsync();
            return WriteStatus(ShopifySessionState.Error,
                "Pending login session expired (5 min). Please start a new login.");
        }

        await _mutex.WaitAsync(ct);
        try
        {
            var page = _pendingLoginPage;
            var context = _pendingLoginContext;
            var targetUrl = _pendingLoginTargetUrl ?? config.FindProductsUrl ?? "https://admin.shopify.com";

            _logger.LogInformation("Verification: submitting code on {Url}", page.Url);

            // Try to find and fill the verification code input
            var codeInput = await page.QuerySelectorAsync(
                "input[autocomplete='one-time-code'], input[name='code'], input[name='otp'], " +
                "input[inputmode='numeric'], input[type='tel'], input[data-ui='verify-code-input'], " +
                "input[type='text'][maxlength='6'], input[type='number']");

            if (codeInput == null)
            {
                // Maybe there are multiple digit inputs (one per digit)
                var digitInputs = await page.QuerySelectorAllAsync("input[maxlength='1'][inputmode='numeric'], input[maxlength='1'][type='tel']");
                if (digitInputs.Count > 0 && digitInputs.Count <= 10)
                {
                    _logger.LogInformation("Verification: found {Count} digit input fields", digitInputs.Count);
                    var digits = code.Where(char.IsDigit).ToArray();
                    for (int i = 0; i < Math.Min(digitInputs.Count, digits.Length); i++)
                    {
                        await digitInputs[i].FillAsync(digits[i].ToString());
                    }
                }
                else
                {
                    // Last resort: just type the code
                    _logger.LogInformation("Verification: no specific input found, typing code");
                    await page.Keyboard.TypeAsync(code.Trim());
                }
            }
            else
            {
                await codeInput.FillAsync(code.Trim());
            }

            // Submit
            var submitBtn = await page.QuerySelectorAsync(
                "button[type='submit'], button:has-text('Verify'), button:has-text('Confirm'), " +
                "button:has-text('Submit'), button:has-text('Log in'), button:has-text('Continue')");
            if (submitBtn != null)
                await SafeClickAsync(submitBtn);
            else
                await page.Keyboard.PressAsync("Enter");

            // Wait for result
            await page.WaitForTimeoutAsync(5000);

            // Check for errors
            var errorEl = await page.QuerySelectorAsync(".error-message, [data-error], .banner--critical, .Polaris-Banner--critical, .notice--error, .Polaris-InlineError");
            if (errorEl != null)
            {
                var errorText = await errorEl.InnerTextAsync();
                if (!string.IsNullOrWhiteSpace(errorText))
                {
                    // Don't dispose — let user retry with correct code
                    return new ShopifySessionStatusDto(
                        State: "verification_required",
                        LastValidatedAt: DateTimeOffset.UtcNow,
                        LastLoggedInAt: null,
                        StorageStateExists: File.Exists(_stateFile),
                        Message: $"Verification failed: {errorText.Trim()}. Try again.");
                }
            }

            // Wait up to 30s for authentication to complete after code submission
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            LoginDiagnostics? lastDiag = null;
            while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                try
                {
                    lastDiag = await LoginStateDetector.DetectAsync(page, config.FindProductsUrl, ct);
                    if (lastDiag.State == LoginState.Authenticated) break;
                    if (lastDiag.State == LoginState.AccountSelection)
                    {
                        await page.GotoAsync(targetUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
                        await page.WaitForTimeoutAsync(3000);
                        lastDiag = await LoginStateDetector.DetectAsync(page, config.FindProductsUrl, ct);
                        if (lastDiag.State == LoginState.Authenticated) break;
                    }
                }
                catch { /* mid-navigation */ }
                await page.WaitForTimeoutAsync(2000);
            }

            if (lastDiag?.State == LoginState.Authenticated)
            {
                await context.StorageStateAsync(new() { Path = _stateFile });
                WriteMeta(DateTimeOffset.UtcNow);
                await DisposePendingLoginAsync();
                return WriteStatus(ShopifySessionState.Connected, "Session saved after verification");
            }

            // Still not auth — check if another verify page
            var stillVerify = (page.Url ?? "").Contains("verify", StringComparison.OrdinalIgnoreCase);
            if (stillVerify)
                return new ShopifySessionStatusDto(
                    State: "verification_required",
                    LastValidatedAt: DateTimeOffset.UtcNow,
                    LastLoggedInAt: null,
                    StorageStateExists: File.Exists(_stateFile),
                    Message: $"Code may be wrong or expired. Current page: {page.Url}. Try again or start a new login.");

            await DisposePendingLoginAsync();
            return WriteStatus(ShopifySessionState.LoginRequired,
                $"Verification did not complete — ended on {lastDiag?.Url ?? page.Url}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SubmitVerificationCodeAsync failed");
            await DisposePendingLoginAsync();
            return WriteStatus(ShopifySessionState.Error, $"Verification error: {ex.Message}");
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task DisposePendingLoginAsync()
    {
        if (_pendingLoginContext != null)
        {
            try { await _pendingLoginContext.DisposeAsync(); } catch { }
            _pendingLoginContext = null;
            _pendingLoginPage = null;
            _pendingLoginTargetUrl = null;
        }
    }

    // ── Manual browser login via noVNC ──

    public async Task<ShopifySessionStatusDto> StartManualBrowserLoginAsync(
        ShopifyAutomationConfig config, CancellationToken ct)
    {
        var targetUrl = config.FindProductsUrl;
        if (string.IsNullOrWhiteSpace(targetUrl))
            targetUrl = config.ShopifyStoreUrl;
        if (string.IsNullOrWhiteSpace(targetUrl))
            targetUrl = "https://accounts.shopify.com/lookup";

        // Clean up any previous manual login
        await StopManualLoginAsync();

        await _mutex.WaitAsync(ct);
        try
        {
            // 1) Find the Chrome binary shipped with Playwright image
            var chromePath = "/ms-playwright/chromium-1148/chrome-linux/chrome";
            if (!File.Exists(chromePath))
            {
                // Fallback: search for it
                var searchResult = "";
                try
                {
                    var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = "bash",
                        Arguments = "-c \"find /ms-playwright -name chrome -type f 2>/dev/null | head -1\"",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                    });
                    if (p != null)
                    {
                        searchResult = (await p.StandardOutput.ReadToEndAsync(ct)).Trim();
                        await p.WaitForExitAsync(ct);
                    }
                }
                catch { }
                if (!string.IsNullOrEmpty(searchResult) && File.Exists(searchResult))
                    chromePath = searchResult;
            }

            // 2) Use a persistent profile dir so Chrome saves cookies
            var profileDir = Path.Combine(_stateDir, "chrome-profile");
            Directory.CreateDirectory(profileDir);

            // Clean up stale lock files from crashed sessions
            foreach (var lockFile in new[] { "SingletonLock", "SingletonSocket", "SingletonCookie" })
            {
                var lf = Path.Combine(profileDir, lockFile);
                try { if (File.Exists(lf)) File.Delete(lf); } catch { }
                try { if (Directory.Exists(lf)) Directory.Delete(lf, true); } catch { }
            }
            // Also clean up stale DevToolsActivePort
            try { var dtp = Path.Combine(profileDir, "DevToolsActivePort"); if (File.Exists(dtp)) File.Delete(dtp); } catch { }

            // Kill any leftover chrome processes
            try { Process.Start("bash", "-c \"pkill -9 -f chrome 2>/dev/null\"")?.WaitForExit(2000); } catch { }
            await Task.Delay(500, ct);

            // 3) Launch raw Chrome (NOT through Playwright — no automation flags!)
            //    with --remote-debugging-port so we can connect later via CDP to grab cookies
            var chromeArgs = string.Join(" ", new[]
            {
                $"--user-data-dir={profileDir}",
                "--remote-debugging-port=9222",
                "--no-sandbox",
                "--disable-dev-shm-usage",
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-background-networking",
                "--disable-sync",
                "--disable-translate",
                "--disable-features=TranslateUI",
                "--window-size=1440,900",
                "--start-maximized",
                "--lang=en-US",
                $"\"{targetUrl}\""
            });

            _logger.LogInformation("Manual login: launching raw Chrome at {Path} with profile {Profile}", chromePath, profileDir);
            _chromeProcess = Process.Start(new ProcessStartInfo
            {
                FileName = chromePath,
                Arguments = chromeArgs,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                Environment = { ["DISPLAY"] = ":99" }
            });

            if (_chromeProcess == null)
                return WriteStatus(ShopifySessionState.Error, "Failed to launch Chrome process");

            // Wait a moment and check it didn't crash immediately
            await Task.Delay(2000, ct);
            if (_chromeProcess.HasExited)
            {
                _logger.LogError("Chrome exited immediately with code {Code}", _chromeProcess.ExitCode);
                return WriteStatus(ShopifySessionState.Error, $"Chrome crashed on launch (exit code {_chromeProcess.ExitCode})");
            }

            _manualLoginTargetUrl = targetUrl;
            _manualLoginExpiry = DateTimeOffset.UtcNow.AddMinutes(10);

            // 4) Start VNC so user can see the browser
            StartVnc();

            // 5) Wait for Chrome's CDP to be ready, then connect Playwright via CDP
            await Task.Delay(3000, ct); // Give Chrome time to start

            _pw ??= await Playwright.CreateAsync();
            Exception? lastCdpError = null;
            for (int i = 0; i < 15; i++)
            {
                try
                {
                    _cdpBrowser = await _pw.Chromium.ConnectOverCDPAsync("http://127.0.0.1:9222");
                    _logger.LogInformation("CDP connected on attempt {Attempt}", i + 1);
                    break;
                }
                catch (Exception ex)
                {
                    lastCdpError = ex;
                    _logger.LogDebug("CDP connect attempt {Attempt} failed: {Msg}", i + 1, ex.Message);
                    await Task.Delay(1000, ct);
                }
            }

            if (_cdpBrowser == null)
            {
                _logger.LogError(lastCdpError, "CDP connection failed after all retries. Chrome running={Running}",
                    _chromeProcess != null && !_chromeProcess.HasExited);
                await StopManualLoginAsync();
                return WriteStatus(ShopifySessionState.Error, $"Chrome started but CDP connection failed: {lastCdpError?.Message}");
            }

            // Grab the first context/page from the already-running Chrome
            var contexts = _cdpBrowser.Contexts;
            if (contexts.Count > 0)
            {
                _manualLoginContext = contexts[0];
                var pages = _manualLoginContext.Pages;
                _manualLoginPage = pages.Count > 0 ? pages[0] : await _manualLoginContext.NewPageAsync();
            }
            else
            {
                _manualLoginContext = await _cdpBrowser.NewContextAsync();
                _manualLoginPage = await _manualLoginContext.NewPageAsync();
                await _manualLoginPage.GotoAsync(targetUrl, new() { Timeout = 30000 });
            }

            _logger.LogInformation("Manual login: Chrome + VNC + CDP ready. User can view at http://localhost:6080/vnc_lite.html");

            return new ShopifySessionStatusDto(
                State: "manual_login_started",
                LastValidatedAt: DateTimeOffset.UtcNow,
                LastLoggedInAt: null,
                StorageStateExists: File.Exists(_stateFile),
                Message: "Browser is open! Log in to Shopify in the browser viewer below. The system will detect when you're done.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartManualBrowserLoginAsync failed");
            await StopManualLoginAsync();
            return WriteStatus(ShopifySessionState.Error, $"Could not start manual login browser: {ex.Message}");
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<ShopifySessionStatusDto> PollManualLoginAsync(
        ShopifyAutomationConfig config, CancellationToken ct)
    {
        if (_manualLoginPage == null || _manualLoginContext == null)
            return new ShopifySessionStatusDto("no_manual_login", null, null, File.Exists(_stateFile),
                "No manual login session active. Click 'Quick Login' to start.");

        if (DateTimeOffset.UtcNow > _manualLoginExpiry)
        {
            await StopManualLoginAsync();
            return WriteStatus(ShopifySessionState.LoginRequired,
                "Manual login session expired (10 min). Try again.");
        }

        try
        {
            var targetUrl = config.FindProductsUrl ?? _manualLoginTargetUrl ?? "https://admin.shopify.com";
            var diag = await LoginStateDetector.DetectAsync(_manualLoginPage, targetUrl, ct);
            _logger.LogInformation("Manual login poll: state={State} url={Url}", diag.State, diag.Url);

            if (diag.State == LoginState.Authenticated)
            {
                // User logged in! Save the session but KEEP the browser alive for automation reuse
                try { await _manualLoginContext.StorageStateAsync(new() { Path = _stateFile }); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not save storage state from CDP context"); }
                WriteMeta(DateTimeOffset.UtcNow);
                _logger.LogInformation("Manual login: SUCCESS — session saved! Browser kept alive for automation.");
                // Stop VNC (user doesn't need to see it anymore) but keep Chrome + CDP alive
                StopVnc();
                return WriteStatus(ShopifySessionState.Connected,
                    "Session saved! You logged in successfully. The browser stays open for automation.");
            }

            if (diag.State == LoginState.AccountSelection)
            {
                // Navigate to the target store
                _logger.LogInformation("Manual login: account picker, navigating to target");
                await _manualLoginPage.GotoAsync(targetUrl, new() { Timeout = 15000 });
            }

            return new ShopifySessionStatusDto(
                State: "manual_login_waiting",
                LastValidatedAt: DateTimeOffset.UtcNow,
                LastLoggedInAt: null,
                StorageStateExists: File.Exists(_stateFile),
                Message: $"Waiting for you to log in… Current page: {diag.Url}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PollManualLoginAsync error (page may be navigating)");
            return new ShopifySessionStatusDto(
                State: "manual_login_waiting",
                LastValidatedAt: DateTimeOffset.UtcNow,
                LastLoggedInAt: null,
                StorageStateExists: File.Exists(_stateFile),
                Message: "Waiting for login… (page is loading)");
        }
    }

    public async Task StopManualLoginAsync()
    {
        // Disconnect CDP (don't dispose context — it belongs to the Chrome process)
        if (_cdpBrowser != null)
        {
            try { await _cdpBrowser.CloseAsync(); } catch { }
            _cdpBrowser = null;
        }
        _manualLoginContext = null;
        _manualLoginPage = null;
        _manualLoginTargetUrl = null;

        // Kill the raw Chrome process
        if (_chromeProcess != null && !_chromeProcess.HasExited)
        {
            try { _chromeProcess.Kill(true); } catch { }
        }
        _chromeProcess = null;

        // Kill any leftover chrome processes
        try { Process.Start("bash", "-c \"pkill -9 -f chrome 2>/dev/null\"")?.WaitForExit(2000); } catch { }

        // Clean up stale lock files
        var profileDir = Path.Combine(_stateDir, "chrome-profile");
        foreach (var lockFile in new[] { "SingletonLock", "SingletonSocket", "SingletonCookie" })
        {
            var lf = Path.Combine(profileDir, lockFile);
            try { if (File.Exists(lf)) File.Delete(lf); } catch { }
        }

        StopVnc();
    }

    public Task<IPage?> GetManualLoginPageAsync(CancellationToken ct)
    {
        if (_manualLoginPage != null && _cdpBrowser != null)
        {
            // Chrome is still alive from manual login — reuse it
            return Task.FromResult<IPage?>(_manualLoginPage);
        }
        // Also check if Chrome process is still running with the profile
        if (_chromeProcess != null && !_chromeProcess.HasExited && _cdpBrowser == null)
        {
            // Chrome is alive but CDP disconnected — try to reconnect
            // This happens if StopManualLoginAsync was called but Chrome wasn't killed
        }
        return Task.FromResult<IPage?>(null);
    }

    private void StartVnc()
    {
        StopVnc();
        try
        {
            // Start x11vnc on display :99 (no password, localhost only not needed since Docker)
            _vncProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "x11vnc",
                Arguments = "-display :99 -forever -nopw -shared -rfbport 5900 -bg -o /dev/null",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            // Give x11vnc a moment to start
            Thread.Sleep(500);

            // Start websockify to bridge VNC→WebSocket on port 6080 with noVNC
            _websockifyProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "websockify",
                Arguments = "--web /usr/share/novnc 6080 localhost:5900",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            _logger.LogInformation("VNC started: x11vnc pid={VncPid}, websockify pid={WsPid}",
                _vncProcess?.Id, _websockifyProcess?.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start VNC");
        }
    }

    private void StopVnc()
    {
        if (_websockifyProcess != null && !_websockifyProcess.HasExited)
        {
            try { _websockifyProcess.Kill(true); } catch { }
            _websockifyProcess = null;
        }
        if (_vncProcess != null && !_vncProcess.HasExited)
        {
            try { _vncProcess.Kill(true); } catch { }
            _vncProcess = null;
        }
        // Also kill any leftover x11vnc/websockify
        try { Process.Start("bash", "-c \"pkill -f x11vnc; pkill -f websockify\"")?.WaitForExit(2000); }
        catch { }
    }

    public async Task<ShopifySessionStatusDto> ImportStorageStateAsync(string storageStateJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(storageStateJson))
            return WriteStatus(ShopifySessionState.Error, "Empty storage state payload");

        // Strip surrounding noise that console / UI copy-paste often adds.
        storageStateJson = storageStateJson.Trim();

        // Remove common prefixes like  STRING_JSON: "..."
        var prefixes = new[] { "STRING_JSON:", "JSON:", "COOKIES:" };
        foreach (var pfx in prefixes)
        {
            if (storageStateJson.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
            {
                storageStateJson = storageStateJson[pfx.Length..].Trim();
                break;
            }
        }

        // Strip surrounding single or double quotes
        if ((storageStateJson.StartsWith('\'') && storageStateJson.EndsWith('\'')) ||
            (storageStateJson.StartsWith('"') && storageStateJson.EndsWith('"') && !storageStateJson.StartsWith("\"{{")))
        {
            storageStateJson = storageStateJson[1..^1];
        }

        // Un-escape if the JSON was double-encoded (e.g. from a console variable).
        if (storageStateJson.StartsWith("\"{\\\"" ) || storageStateJson.StartsWith("\\{" ))
        {
            try { storageStateJson = System.Text.Json.JsonSerializer.Deserialize<string>(storageStateJson) ?? storageStateJson; }
            catch { /* keep as-is */ }
        }

        // Accept either a raw Playwright storage state or a common "cookies only" JSON.
        string normalised;
        int cookieCount = 0;
        bool hasAuthCookies = false;
        try
        {
            using var doc = JsonDocument.Parse(storageStateJson);

            JsonElement cookiesElement;
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("cookies", out cookiesElement))
            {
                // Already a storage-state shape — normalise cookies in place.
                var normalisedCookies = NormaliseCookieArray(cookiesElement);
                cookieCount = normalisedCookies.Count;
                hasAuthCookies = normalisedCookies.Any(IsAuthCookie);
                var state = new { cookies = normalisedCookies, origins = doc.RootElement.TryGetProperty("origins", out var o) ? o.Clone() : JsonDocument.Parse("[]").RootElement };
                normalised = JsonSerializer.Serialize(state);
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                // Cookie-only array (e.g. Cookie-Editor export) — normalise & wrap.
                var normalisedCookies = NormaliseCookieArray(doc.RootElement);
                cookieCount = normalisedCookies.Count;
                hasAuthCookies = normalisedCookies.Any(IsAuthCookie);
                var wrapped = new { cookies = normalisedCookies, origins = Array.Empty<object>() };
                normalised = JsonSerializer.Serialize(wrapped);
            }
            else
            {
                return WriteStatus(ShopifySessionState.Error,
                    "Payload must be a Playwright storage-state object or a cookies array");
            }
        }
        catch (JsonException ex)
        {
            return WriteStatus(ShopifySessionState.Error, $"Invalid JSON: {ex.Message}");
        }

        if (cookieCount == 0)
            return WriteStatus(ShopifySessionState.Error, "No cookies found in the payload");

        if (!hasAuthCookies)
        {
            _logger.LogWarning("Imported {Count} cookies but none are Shopify auth cookies — session will likely fail", cookieCount);
            // Still save them — the validate step will confirm
        }

        await _mutex.WaitAsync(ct);
        try
        {
            await File.WriteAllTextAsync(_stateFile, normalised, ct);
            WriteMeta(DateTimeOffset.UtcNow);
            _logger.LogInformation("Imported {Count} cookies (auth={HasAuth}) to {Path}", cookieCount, hasAuthCookies, _stateFile);

            if (!hasAuthCookies)
                return WriteStatus(ShopifySessionState.LoginRequired,
                    $"Imported {cookieCount} cookies but NONE are Shopify auth cookies (need _secure_admin_session_id_*, _master_udr, or shopify_user_t). " +
                    "The cookieStore/document.cookie APIs cannot read HttpOnly cookies. Use the Cookie-Editor extension or Playwright codegen instead.");

            return WriteStatus(ShopifySessionState.Connected, $"Session imported ({cookieCount} cookies including auth) — validate to confirm");
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IBrowserContext> OpenAuthenticatedContextAsync(IBrowser browser, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            return await browser.NewContextAsync(new()
            {
                StorageStatePath = File.Exists(_stateFile) ? _stateFile : null,
                ViewportSize = new() { Width = 1440, Height = 900 },
                UserAgent = ConsistentUserAgent,
            });
        }
        finally { _mutex.Release(); }
    }

    /// <summary>Persists any rotated cookies back to the storage-state file so the
    /// next context creation picks them up.</summary>
    public async Task SaveContextAsync(IBrowserContext context, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            await context.StorageStateAsync(new() { Path = _stateFile });
            _logger.LogDebug("Saved rotated cookies back to {Path}", _stateFile);
        }
        finally { _mutex.Release(); }
    }

    private async Task<IBrowser> GetHeadlessBrowserAsync(CancellationToken ct)
    {
        _pw ??= await Playwright.CreateAsync();
        _headlessBrowser ??= await _pw.Chromium.LaunchAsync(new()
        {
            Headless = true,
            Args = new[]
            {
                "--disable-blink-features=AutomationControlled",
                "--no-sandbox",
                "--disable-dev-shm-usage",
                "--disable-infobars",
                "--window-size=1440,900",
                "--disable-extensions",
                "--lang=en-US,en",
                "--disable-features=IsolateOrigins,site-per-process",
                "--disable-setuid-sandbox",
                "--disable-accelerated-2d-canvas",
                "--no-first-run",
                "--no-zygote",
                "--disable-background-networking",
                "--disable-backgrounding-occluded-windows",
                "--disable-renderer-backgrounding",
            }
        });
        return _headlessBrowser;
    }

    /// <summary>Gets or creates a HEADED browser for credential login — runs on Xvfb virtual display.
    /// Headed mode is much harder for anti-bot systems to detect than headless.</summary>
    private async Task<IBrowser> GetHeadedLoginBrowserAsync(CancellationToken ct)
    {
        _pw ??= await Playwright.CreateAsync();
        // Dispose old headed browser if it exists (fresh per login attempt to avoid stale state)
        if (_headedBrowser != null)
        {
            try { await _headedBrowser.DisposeAsync(); } catch { }
            _headedBrowser = null;
        }
        _headedBrowser = await _pw.Chromium.LaunchAsync(new()
        {
            Headless = false,  // Real headed browser on Xvfb
            Args = new[]
            {
                "--disable-blink-features=AutomationControlled",
                "--no-sandbox",
                "--disable-dev-shm-usage",
                "--disable-infobars",
                "--window-size=1440,900",
                "--start-maximized",
                "--lang=en-US,en",
                "--disable-setuid-sandbox",
                "--no-first-run",
                "--disable-background-networking",
            }
        });
        return _headedBrowser;
    }

    /// <summary>
    /// Injects anti-detection scripts into a page to make headless Chromium
    /// look like a real browser to anti-bot systems.
    /// </summary>
    private static async Task InjectStealthAsync(IPage page)
    {
        // Override navigator.webdriver
        await page.AddInitScriptAsync(@"
            // Remove webdriver flag
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            
            // Override permissions API
            const originalQuery = window.navigator.permissions.query;
            window.navigator.permissions.query = (parameters) =>
                parameters.name === 'notifications'
                    ? Promise.resolve({ state: Notification.permission })
                    : originalQuery(parameters);
            
            // Fake plugins array
            Object.defineProperty(navigator, 'plugins', {
                get: () => [1, 2, 3, 4, 5].map(() => ({
                    description: '',
                    filename: '',
                    length: 0,
                    name: '',
                    item: () => null,
                    namedItem: () => null,
                    refresh: () => {},
                    [Symbol.iterator]: function*() {}
                }))
            });
            
            // Fake languages
            Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
            
            // Chrome runtime
            window.chrome = { runtime: {}, loadTimes: () => ({}), csi: () => ({}) };
            
            // WebGL vendor
            const getParameter = WebGLRenderingContext.prototype.getParameter;
            WebGLRenderingContext.prototype.getParameter = function(parameter) {
                if (parameter === 37445) return 'Intel Inc.';
                if (parameter === 37446) return 'Intel Iris OpenGL Engine';
                return getParameter.call(this, parameter);
            };
            
            // Connection info
            Object.defineProperty(navigator, 'connection', {
                get: () => ({ effectiveType: '4g', rtt: 50, downlink: 10, saveData: false })
            });
            
            // Hardware concurrency
            Object.defineProperty(navigator, 'hardwareConcurrency', { get: () => 8 });
            
            // Device memory
            Object.defineProperty(navigator, 'deviceMemory', { get: () => 8 });
        ");
    }

    /// <summary>Simulates human-like typing with random delays between keystrokes,
    /// including moving the mouse to the element first.</summary>
    private static async Task HumanTypeAsync(IPage page, IElementHandle element, string text)
    {
        await MoveMouseToElementAsync(page, element);
        await SafeClickAsync(element);
        await page.WaitForTimeoutAsync(new Random().Next(200, 500));
        var rng = new Random();
        foreach (var ch in text)
        {
            await page.Keyboard.PressAsync(ch.ToString());
            await page.WaitForTimeoutAsync(rng.Next(50, 180));
        }
    }

    /// <summary>Moves the mouse to an element with human-like curve (multiple steps).</summary>
    private static async Task MoveMouseToElementAsync(IPage page, IElementHandle element)
    {
        try
        {
            var box = await element.BoundingBoxAsync();
            if (box == null) return;
            var rng = new Random();
            var targetX = box.X + box.Width / 2 + rng.Next(-5, 5);
            var targetY = box.Y + box.Height / 2 + rng.Next(-3, 3);

            // Move in 3-5 steps to simulate natural mouse movement
            var steps = rng.Next(3, 6);
            for (int i = 1; i <= steps; i++)
            {
                // Add slight randomness to the path
                var jitterX = rng.Next(-10, 10) * (1.0 - (double)i / steps);
                var jitterY = rng.Next(-5, 5) * (1.0 - (double)i / steps);
                var x = targetX * ((double)i / steps) + jitterX;
                var y = targetY * ((double)i / steps) + jitterY;
                await page.Mouse.MoveAsync((float)x, (float)y);
                await page.WaitForTimeoutAsync(rng.Next(20, 60));
            }
            // Final move to exact target
            await page.Mouse.MoveAsync((float)targetX, (float)targetY);
        }
        catch { /* element may not have bounding box */ }
    }

    /// <summary>
    /// Throws if the current page is a Cloudflare / anti-bot interstitial. These pages
    /// never progress to the login form, so waiting on them burns our timeout budget.
    /// We surface a clear error so the UI can tell the user to complete the challenge
    /// in the VNC window or retry with fresh cookies.
    /// </summary>
    private static async Task FailIfCloudflareAsync(IPage page)
    {
        try
        {
            var title = await page.TitleAsync();
            var url = page.Url ?? "";
            var titleLower = (title ?? "").ToLowerInvariant();
            if (titleLower.Contains("just a moment") ||
                titleLower.Contains("attention required") ||
                titleLower.Contains("cloudflare") ||
                url.Contains("__cf_chl") ||
                url.Contains("cdn-cgi/challenge-platform"))
            {
                throw new InvalidOperationException(
                    $"Cloudflare challenge detected on {url} (title: '{title}'). " +
                    "Shopify's anti-bot blocked the automated browser. Try again after a few minutes, " +
                    "or use the Manual Login browser (VNC window) to complete the challenge by hand.");
            }
        }
        catch (InvalidOperationException) { throw; }
        catch { /* page may be mid-nav */ }
    }

    /// <summary>
    /// Robust click: scroll into view, try a real click with Force=true (skips the
    /// "element inside viewport" Playwright check that was failing on Shopify's
    /// fixed-positioned login buttons), then fall back to JS click. Shopify's login
    /// page recently gained a sticky footer that puts the Continue/Log-in button
    /// outside the default viewport — Force bypasses that.
    /// </summary>
    private static async Task SafeClickAsync(IElementHandle element)
    {
        try { await element.ScrollIntoViewIfNeededAsync(new() { Timeout = 3000 }); } catch { }
        try
        {
            await element.ClickAsync(new() { Timeout = 3000, Force = true });
            return;
        }
        catch { /* fall through to JS click */ }

        try { await element.EvaluateAsync("el => el.click()"); }
        catch
        {
            // Last resort: dispatch a synthesised click event.
            await element.EvaluateAsync("el => el.dispatchEvent(new MouseEvent('click', {bubbles:true, cancelable:true}))");
        }
    }

    // ── status/meta persistence ──

    private ShopifySessionStatusDto ReadStatus()
    {
        ShopifySessionState state = File.Exists(_stateFile)
            ? ShopifySessionState.Unknown
            : ShopifySessionState.LoginRequired;
        DateTimeOffset? lastValidated = null, lastLoggedIn = null;
        string? message = null;

        if (File.Exists(_metaFile))
        {
            try
            {
                var meta = JsonSerializer.Deserialize<SessionMeta>(File.ReadAllText(_metaFile));
                if (meta != null)
                {
                    if (Enum.TryParse<ShopifySessionState>(meta.State, true, out var s)) state = s;
                    lastValidated = meta.LastValidatedAt;
                    lastLoggedIn = meta.LastLoggedInAt;
                    message = meta.Message;
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not read session meta"); }
        }

        return new ShopifySessionStatusDto(
            State: state.ToString().ToLowerInvariant().Replace("loginrequired", "login_required"),
            LastValidatedAt: lastValidated,
            LastLoggedInAt: lastLoggedIn,
            StorageStateExists: File.Exists(_stateFile),
            Message: message);
    }

    private ShopifySessionStatusDto WriteStatus(ShopifySessionState state, string? message)
    {
        var existing = File.Exists(_metaFile)
            ? (JsonSerializer.Deserialize<SessionMeta>(File.ReadAllText(_metaFile)) ?? new())
            : new SessionMeta();
        existing.State = state.ToString();
        existing.LastValidatedAt = DateTimeOffset.UtcNow;
        if (state == ShopifySessionState.Connected) existing.LastLoggedInAt = DateTimeOffset.UtcNow;
        existing.Message = message;
        File.WriteAllText(_metaFile, JsonSerializer.Serialize(existing));
        return new ShopifySessionStatusDto(
            State: state.ToString().ToLowerInvariant().Replace("loginrequired", "login_required"),
            LastValidatedAt: existing.LastValidatedAt,
            LastLoggedInAt: existing.LastLoggedInAt,
            StorageStateExists: File.Exists(_stateFile),
            Message: message);
    }

    private void WriteMeta(DateTimeOffset loggedInAt)
    {
        var existing = File.Exists(_metaFile)
            ? (JsonSerializer.Deserialize<SessionMeta>(File.ReadAllText(_metaFile)) ?? new())
            : new SessionMeta();
        existing.LastLoggedInAt = loggedInAt;
        File.WriteAllText(_metaFile, JsonSerializer.Serialize(existing));
    }

    public async ValueTask DisposeAsync()
    {
        await StopManualLoginAsync();
        if (_headedBrowser != null) await _headedBrowser.DisposeAsync();
        if (_headlessBrowser != null) await _headlessBrowser.DisposeAsync();
        _pw?.Dispose();
        _mutex.Dispose();
    }

    private sealed class SessionMeta
    {
        public string State { get; set; } = "Unknown";
        public DateTimeOffset? LastValidatedAt { get; set; }
        public DateTimeOffset? LastLoggedInAt { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>Known Shopify auth cookie name prefixes/names. These are HttpOnly.</summary>
    private static readonly string[] AuthCookiePrefixes = {
        "_secure_admin_session_id",
        "_master_udr",
        "shopify_user_t",
        "koa.sid",
        "_shopify_admin_",
    };

    private static bool IsAuthCookie(Dictionary<string, object> cookie)
    {
        if (!cookie.TryGetValue("name", out var nameObj) || nameObj is not string name)
            return false;
        return AuthCookiePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Normalises a JSON array of cookies from various sources (Cookie-Editor, Playwright,
    /// cookieStore API, etc.) into the Playwright storage-state format.
    /// Cookie-Editor uses "expirationDate" (epoch seconds), "hostOnly", "storeId", "session",
    /// "id" — Playwright expects "expires" (epoch seconds, -1 for session), "httpOnly", etc.
    /// </summary>
    private List<Dictionary<string, object>> NormaliseCookieArray(JsonElement arr)
    {
        var result = new List<Dictionary<string, object>>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var cookie = new Dictionary<string, object>();

            // Copy known Playwright fields
            CopyString(el, cookie, "name");
            CopyString(el, cookie, "value");
            CopyString(el, cookie, "path", "/");
            CopyBool(el, cookie, "secure", false);
            CopyBool(el, cookie, "httpOnly", false);

            // Domain: Cookie-Editor uses "domain" (may start with "."), Playwright uses "domain"
            if (el.TryGetProperty("domain", out var domProp))
                cookie["domain"] = domProp.GetString() ?? "";
            else
                cookie["domain"] = "";

            // SameSite: normalise to Playwright values (Strict/Lax/None)
            if (el.TryGetProperty("sameSite", out var ssProp))
            {
                var ss = ssProp.GetString() ?? "Lax";
                // Cookie-Editor uses lowercase: "lax", "strict", "none", "no_restriction"
                // Convert "no_restriction" → "None"
                if (ss.Equals("no_restriction", StringComparison.OrdinalIgnoreCase))
                    ss = "None";
                else if (ss.Length > 0)
                    ss = char.ToUpper(ss[0]) + ss[1..].ToLower();
                cookie["sameSite"] = ss;
            }
            else
            {
                cookie["sameSite"] = "Lax";
            }

            // Expires: Cookie-Editor uses "expirationDate" (epoch float), Playwright uses "expires" (epoch float, -1=session)
            if (el.TryGetProperty("expires", out var expProp) && expProp.ValueKind == JsonValueKind.Number)
                cookie["expires"] = expProp.GetDouble();
            else if (el.TryGetProperty("expirationDate", out var expDateProp) && expDateProp.ValueKind == JsonValueKind.Number)
                cookie["expires"] = expDateProp.GetDouble();
            else
                cookie["expires"] = -1;

            // Only add cookies that have name & value
            if (cookie.TryGetValue("name", out var n) && n is string nameStr && !string.IsNullOrEmpty(nameStr))
                result.Add(cookie);
        }
        return result;
    }

    private static void CopyString(JsonElement src, Dictionary<string, object> dst, string key, string fallback = "")
    {
        if (src.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            dst[key] = prop.GetString() ?? fallback;
        else
            dst[key] = fallback;
    }

    private static void CopyBool(JsonElement src, Dictionary<string, object> dst, string key, bool fallback)
    {
        if (src.TryGetProperty(key, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) dst[key] = true;
            else if (prop.ValueKind == JsonValueKind.False) dst[key] = false;
            else dst[key] = fallback;
        }
        else
            dst[key] = fallback;
    }
}
