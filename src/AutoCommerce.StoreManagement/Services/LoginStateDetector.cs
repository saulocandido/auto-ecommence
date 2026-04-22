using Microsoft.Playwright;

namespace AutoCommerce.StoreManagement.Services;

public enum LoginState
{
    /// Page looks like the app is loaded and we are inside admin.shopify.com on the expected store + app.
    Authenticated,
    /// Page is the Shopify login page (email/password fields or accounts.shopify.com).
    LoginPage,
    /// Page is the "Choose an account / Select store" picker.
    AccountSelection,
    /// Page is reachable but not the app we expected (wrong URL, error page, etc).
    NotInApp,
    /// Couldn't classify — treat as "maybe authenticated, try once".
    Unknown,
}

public record LoginDiagnostics(
    LoginState State,
    string Url,
    string Title,
    bool SawEmailField,
    bool SawPasswordField,
    bool SawSignInButton,
    bool SawAccountChooser,
    bool InsideAdminOrigin,
    bool OnTargetPath,
    string? Notes);

/// <summary>
/// Central place that decides whether a Playwright-controlled page is authenticated
/// against the Shopify admin app. Every step in the automation calls
/// <see cref="DetectAsync"/> BEFORE retrying its own selector — so "search input not
/// found" no longer masks "you're actually on the login page".
///
/// "Authenticated" is strict: we must have landed on a URL that shares the same
/// /store/&lt;shop&gt;/apps/&lt;app&gt;/ prefix as the target. Landing on
/// admin.shopify.com/store (the store picker) or admin.shopify.com/ (bare admin)
/// is classified as AccountSelection, not Authenticated — that was the false
/// positive that caused Test to report "connected" while the automation then
/// hit the actual login page.
/// </summary>
public static class LoginStateDetector
{
    private static readonly string[] LoginHostHints =
    {
        "accounts.shopify.com",
        "identity.shopify.com",
        "/account/login",
        "/auth/login",
        "/login",
    };

    public static async Task<LoginDiagnostics> DetectAsync(
        IPage page, string? targetUrl = null, CancellationToken ct = default)
    {
        string url = "";
        string title = "";
        try { url = page.Url ?? ""; } catch { /* page may be closing */ }
        try { title = await page.TitleAsync(); } catch { /* ignore */ }

        var insideAdminOrigin = url.StartsWith("https://admin.shopify.com/", StringComparison.OrdinalIgnoreCase);

        bool emailField = false, passwordField = false, signInBtn = false, accountChooser = false;
        try
        {
            emailField = await page.QuerySelectorAsync(
                "input[type='email'], input[name='account[email]'], input[autocomplete='username']") != null;
            passwordField = await page.QuerySelectorAsync(
                "input[type='password']") != null;
            signInBtn = await page.QuerySelectorAsync(
                "button:has-text('Log in'), button:has-text('Sign in'), button[type='submit'][name='commit']") != null;
            accountChooser = await page.QuerySelectorAsync(
                ":has-text('Choose an account'), :has-text('Select store'), :has-text('Choose a store'), [data-test='account-picker']") != null;
        }
        catch { /* navigation races — decide based on URL alone */ }

        var (state, onTarget, notes) = Classify(url, targetUrl, emailField, passwordField, accountChooser);

        return new LoginDiagnostics(
            state, url, title,
            emailField, passwordField, signInBtn, accountChooser,
            insideAdminOrigin, onTarget, notes);
    }

    /// <summary>
    /// Pure classifier — no Playwright dependency, easy to unit test.
    /// When <paramref name="targetUrl"/> is provided, "Authenticated" requires that
    /// the landed URL shares the same /store/&lt;shop&gt;/apps/&lt;app&gt;/ prefix.
    /// </summary>
    public static (LoginState State, bool OnTarget, string? Notes) Classify(
        string? url, string? targetUrl, bool emailField, bool passwordField, bool accountChooser)
    {
        var urlLower = (url ?? "").ToLowerInvariant();
        var urlLooksLikeLogin = LoginHostHints.Any(h => urlLower.Contains(h));
        var insideAdminOrigin = urlLower.StartsWith("https://admin.shopify.com/");

        // Store-picker / bare-admin landing pages — these look authenticated at the
        // hostname level but mean "pick a store" or "not enough session to reach app".
        // We classify them as AccountSelection so the UI tells the user to log in
        // again properly rather than "connected" followed by a silent redirect.
        var isStorePicker = urlLower == "https://admin.shopify.com/"
                         || urlLower == "https://admin.shopify.com/store"
                         || urlLower.StartsWith("https://admin.shopify.com/store?")
                         || urlLower.StartsWith("https://admin.shopify.com/setup");

        if (urlLooksLikeLogin || (emailField && passwordField))
            return (LoginState.LoginPage, false, "login page detected");
        if (accountChooser || isStorePicker)
            return (LoginState.AccountSelection, false,
                isStorePicker ? $"store picker shown ({url}) — session exists but not scoped to the target store"
                              : "account picker detected");
        if (!insideAdminOrigin && !string.IsNullOrEmpty(url))
            return (LoginState.NotInApp, false, $"not on admin.shopify.com (was: {url})");
        if (!insideAdminOrigin)
            return (LoginState.Unknown, false, "could not classify page");

        // insideAdminOrigin == true from here on. Require target-path match.
        var onTarget = IsOnTargetPath(urlLower, targetUrl);
        if (!onTarget && !string.IsNullOrEmpty(targetUrl))
            return (LoginState.NotInApp, false,
                $"landed on admin.shopify.com but not on target app path (was: {url})");
        return (LoginState.Authenticated, true, null);
    }

    /// <summary>
    /// True when <paramref name="landedLower"/> shares the /store/&lt;shop&gt;/apps/&lt;app&gt;/
    /// prefix of the target — i.e. we're inside the right app for the right store.
    /// If <paramref name="targetUrl"/> is null, any admin URL passes (legacy behaviour).
    /// </summary>
    internal static bool IsOnTargetPath(string landedLower, string? targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl)) return true;
        var tLower = targetUrl.ToLowerInvariant();
        // Prefix through /apps/<slug>/ if present — if the user ends up on a different
        // page under the same app we still consider them authenticated.
        var appsIdx = tLower.IndexOf("/apps/");
        if (appsIdx < 0) return landedLower.StartsWith(tLower);
        var slashAfterApp = tLower.IndexOf('/', appsIdx + "/apps/".Length);
        if (slashAfterApp < 0) slashAfterApp = tLower.Length;
        var prefix = tLower[..slashAfterApp];
        return landedLower.StartsWith(prefix);
    }
}

/// <summary>
/// Thrown by automation steps when the Playwright page is not authenticated — the
/// run orchestrator catches this and transitions the run to <c>LoginRequired</c>
/// instead of retrying a selector that will never appear.
/// </summary>
public class SessionExpiredException : Exception
{
    public LoginDiagnostics Diagnostics { get; }
    public SessionExpiredException(LoginDiagnostics diagnostics)
        : base($"Shopify session is not authenticated ({diagnostics.State}): {diagnostics.Notes ?? diagnostics.Url}")
    {
        Diagnostics = diagnostics;
    }
}
