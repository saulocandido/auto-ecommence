using System.Text.Json;
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
            var diag = await LoginStateDetector.DetectAsync(page, ct);

            // Persist any rotated cookies back to disk so automation picks them up.
            if (diag.State == LoginState.Authenticated)
            {
                try { await context.StorageStateAsync(new() { Path = _stateFile }); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist rotated cookies after validation"); }
            }

            return diag.State switch
            {
                LoginState.Authenticated => WriteStatus(ShopifySessionState.Connected, $"Authenticated on {diag.Url}"),
                LoginState.LoginPage => WriteStatus(ShopifySessionState.LoginRequired, "Shopify login page shown — session expired"),
                LoginState.AccountSelection => WriteStatus(ShopifySessionState.LoginRequired, "Account picker shown — resume login"),
                LoginState.NotInApp => WriteStatus(ShopifySessionState.LoginRequired, diag.Notes ?? "Not in admin app"),
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
                    lastDiag = await LoginStateDetector.DetectAsync(page, ct);
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
        try
        {
            using var doc = JsonDocument.Parse(storageStateJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("cookies", out _))
            {
                // Already a storage-state shape.
                normalised = storageStateJson;
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                // Cookie-only array — wrap it.
                var wrapped = new { cookies = doc.RootElement.Clone(), origins = Array.Empty<object>() };
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

        await _mutex.WaitAsync(ct);
        try
        {
            await File.WriteAllTextAsync(_stateFile, normalised, ct);
            WriteMeta(DateTimeOffset.UtcNow);
            _logger.LogInformation("Imported Shopify storage state to {Path}", _stateFile);
            return WriteStatus(ShopifySessionState.Connected, "Session imported — validate to confirm it works");
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
            Args = new[] { "--disable-blink-features=AutomationControlled", "--no-sandbox" }
        });
        return _headlessBrowser;
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
}
