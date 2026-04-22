using Microsoft.Playwright;

namespace AutoCommerce.StoreManagement.Services;

public enum LoginState
{
    /// Page looks like the app is loaded and we are inside admin.shopify.com.
    Authenticated,
    /// Page is the Shopify login page (email/password fields or accounts.shopify.com).
    LoginPage,
    /// Page is the "Choose an account" selector.
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
    string? Notes);

/// <summary>
/// Central place that decides whether a Playwright-controlled page is authenticated
/// against the Shopify admin app. Every step in the automation calls
/// <see cref="DetectAsync"/> BEFORE retrying its own selector — so "search input not
/// found" no longer masks "you're actually on the login page".
/// </summary>
public static class LoginStateDetector
{
    // Hostnames/paths that positively identify a login flow.
    private static readonly string[] LoginHostHints =
    {
        "accounts.shopify.com",
        "identity.shopify.com",
        "/account/login",
        "/auth/login",
        "/login",
    };

    public static async Task<LoginDiagnostics> DetectAsync(IPage page, CancellationToken ct = default)
    {
        string url = "";
        string title = "";
        try { url = page.Url ?? ""; } catch { /* page may be closing */ }
        try { title = await page.TitleAsync(); } catch { /* ignore */ }

        var insideAdminOrigin = url.StartsWith("https://admin.shopify.com/", StringComparison.OrdinalIgnoreCase);

        var urlLower = url.ToLowerInvariant();
        var urlLooksLikeLogin = LoginHostHints.Any(h => urlLower.Contains(h));

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
                ":has-text('Choose an account'), :has-text('Select account'), [data-test='account-picker']") != null;
        }
        catch { /* navigation races — decide based on URL alone */ }

        var (state, notes) = Classify(url, emailField, passwordField, accountChooser);

        return new LoginDiagnostics(
            state, url, title,
            emailField, passwordField, signInBtn, accountChooser,
            insideAdminOrigin, notes);
    }

    /// <summary>
    /// Pure classifier — no Playwright dependency, easy to unit test.
    /// Exposes the decision tree the live detector uses.
    /// </summary>
    public static (LoginState State, string? Notes) Classify(
        string? url, bool emailField, bool passwordField, bool accountChooser)
    {
        var urlLower = (url ?? "").ToLowerInvariant();
        var urlLooksLikeLogin = LoginHostHints.Any(h => urlLower.Contains(h));
        var insideAdminOrigin = urlLower.StartsWith("https://admin.shopify.com/");

        if (urlLooksLikeLogin || (emailField && passwordField))
            return (LoginState.LoginPage, "login page detected");
        if (accountChooser)
            return (LoginState.AccountSelection, "account picker detected");
        if (!insideAdminOrigin && !string.IsNullOrEmpty(url))
            return (LoginState.NotInApp, $"not on admin.shopify.com (was: {url})");
        if (insideAdminOrigin)
            return (LoginState.Authenticated, null);
        return (LoginState.Unknown, "could not classify page");
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
