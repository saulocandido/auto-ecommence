using System.Net;
using System.Text;
using System.Text.Json;
using AutoCommerce.StoreManagement.Domain;

namespace AutoCommerce.StoreManagement.Services;

/// <summary>
/// Candidate product surfaced by the Shopify admin app's "find products" search.
/// Supplier / image / description are best-effort — the underlying app doesn't
/// always expose every field on the list view.
/// </summary>
public record AdminAppSearchCandidate(
    string ExternalId,
    string Title,
    decimal Price,
    string? Vendor,
    string? ImageUrl,
    string? Description);

public record AdminAppImportListItem(
    string ImportItemId,
    string ExternalId,
    string Title);

public record AdminAppPushResult(
    bool Success,
    long? ShopifyProductId,
    string? Error);

/// <summary>
/// Drives the three‑step flow that the UI automates:
///   1. <see cref="SearchAsync"/>                — <c>/apps/dropshipper-ai/app/find-products</c>
///   2. <see cref="AddToImportListAsync"/>       — clicks "add to import list" on a candidate
///   3. <see cref="GetImportListAsync"/>         — <c>/apps/dropshipper-ai/app/import-list</c>
///   4. <see cref="PushToStoreAsync"/>           — clicks "push to store" on an import item
///
/// The production client hits Shopify admin directly; tests can substitute a fake.
/// </summary>
public interface IShopifyAdminAppClient
{
    Task<IReadOnlyList<AdminAppSearchCandidate>> SearchAsync(
        ShopifyAutomationConfig config, string query, CancellationToken ct);

    Task<string> AddToImportListAsync(
        ShopifyAutomationConfig config, string externalId, CancellationToken ct);

    Task<IReadOnlyList<AdminAppImportListItem>> GetImportListAsync(
        ShopifyAutomationConfig config, CancellationToken ct);

    Task<AdminAppPushResult> PushToStoreAsync(
        ShopifyAutomationConfig config, string importItemId, CancellationToken ct);
}

/// <summary>
/// HTTP-based implementation. The admin app's internal endpoints are not part of
/// a public contract — we keep the call surface narrow (GET/POST JSON with the
/// configured cookie / bearer token) and surface explicit errors when a step
/// fails, so the caller can decide between retry / manual-review / fail.
///
/// For end-to-end scenarios where only browser automation will work, inject a
/// Playwright-based implementation of <see cref="IShopifyAdminAppClient"/>;
/// the automation service doesn't care which flavour is plugged in.
/// </summary>
public class HttpShopifyAdminAppClient : IShopifyAdminAppClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<HttpShopifyAdminAppClient> _logger;

    public HttpShopifyAdminAppClient(IHttpClientFactory httpFactory, ILogger<HttpShopifyAdminAppClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AdminAppSearchCandidate>> SearchAsync(
        ShopifyAutomationConfig config, string query, CancellationToken ct)
    {
        var url = AppendQuery(config.FindProductsUrl, $"q={Uri.EscapeDataString(query)}");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuth(req, config);

        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("find-products search returned {Status} for query={Query}", resp.StatusCode, query);
            return Array.Empty<AdminAppSearchCandidate>();
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        return ParseSearchResponse(body);
    }

    public async Task<string> AddToImportListAsync(
        ShopifyAutomationConfig config, string externalId, CancellationToken ct)
    {
        var url = AppendPath(config.FindProductsUrl, "import");
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { externalId }),
                Encoding.UTF8, "application/json")
        };
        AddAuth(req, config);

        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        if (doc.RootElement.TryGetProperty("importItemId", out var idProp) && idProp.ValueKind == JsonValueKind.String)
            return idProp.GetString()!;

        // If the upstream responds with 2xx but no id, synthesise one so the run can still
        // proceed to the import-list step and look up by externalId.
        return externalId;
    }

    public async Task<IReadOnlyList<AdminAppImportListItem>> GetImportListAsync(
        ShopifyAutomationConfig config, CancellationToken ct)
    {
        var url = config.ImportListUrl;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuth(req, config);

        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("import-list returned {Status}", resp.StatusCode);
            return Array.Empty<AdminAppImportListItem>();
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        return ParseImportListResponse(body);
    }

    public async Task<AdminAppPushResult> PushToStoreAsync(
        ShopifyAutomationConfig config, string importItemId, CancellationToken ct)
    {
        var url = AppendPath(config.ImportListUrl, $"{Uri.EscapeDataString(importItemId)}/push");
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        AddAuth(req, config);

        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(2);

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            return new AdminAppPushResult(false, null, $"HTTP {(int)resp.StatusCode}: {Truncate(body, 300)}");

        long? shopifyId = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (doc.RootElement.TryGetProperty("shopifyProductId", out var idProp))
            {
                if (idProp.ValueKind == JsonValueKind.Number && idProp.TryGetInt64(out var n)) shopifyId = n;
                else if (idProp.ValueKind == JsonValueKind.String && long.TryParse(idProp.GetString(), out var s)) shopifyId = s;
            }
        }
        catch (JsonException) { /* non-JSON success body is fine — we still report success */ }

        return new AdminAppPushResult(true, shopifyId, null);
    }

    // ── helpers ──

    private static string AppendPath(string baseUrl, string segment) =>
        (baseUrl ?? "").TrimEnd('/') + "/" + segment.TrimStart('/');

    private static string AppendQuery(string baseUrl, string query) =>
        (baseUrl ?? "") + ((baseUrl ?? "").Contains('?') ? "&" : "?") + query;

    private static void AddAuth(HttpRequestMessage req, ShopifyAutomationConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.SessionCookie))
            req.Headers.TryAddWithoutValidation("Cookie", config.SessionCookie);
        if (!string.IsNullOrWhiteSpace(config.AuthToken))
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.AuthToken}");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
    }

    private static IReadOnlyList<AdminAppSearchCandidate> ParseSearchResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<AdminAppSearchCandidate>();

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!TryGetArray(doc.RootElement, out var arr))
                return Array.Empty<AdminAppSearchCandidate>();

            var list = new List<AdminAppSearchCandidate>();
            foreach (var item in arr.EnumerateArray())
            {
                var id = GetString(item, "externalId", "id") ?? Guid.NewGuid().ToString();
                var title = GetString(item, "title", "name") ?? "(untitled)";
                var price = GetDecimal(item, "price", "cost") ?? 0m;
                var vendor = GetString(item, "vendor", "supplier", "supplierKey");
                var image = GetString(item, "imageUrl", "image", "thumbnail");
                var desc = GetString(item, "description", "summary");
                list.Add(new AdminAppSearchCandidate(id, title, price, vendor, image, desc));
            }
            return list;
        }
        catch (JsonException)
        {
            return Array.Empty<AdminAppSearchCandidate>();
        }
    }

    private static IReadOnlyList<AdminAppImportListItem> ParseImportListResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<AdminAppImportListItem>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!TryGetArray(doc.RootElement, out var arr))
                return Array.Empty<AdminAppImportListItem>();

            var list = new List<AdminAppImportListItem>();
            foreach (var item in arr.EnumerateArray())
            {
                var importId = GetString(item, "importItemId", "id") ?? Guid.NewGuid().ToString();
                var external = GetString(item, "externalId", "productExternalId") ?? importId;
                var title = GetString(item, "title", "name") ?? "(untitled)";
                list.Add(new AdminAppImportListItem(importId, external, title));
            }
            return list;
        }
        catch (JsonException)
        {
            return Array.Empty<AdminAppImportListItem>();
        }
    }

    private static bool TryGetArray(JsonElement root, out JsonElement arr)
    {
        if (root.ValueKind == JsonValueKind.Array) { arr = root; return true; }
        foreach (var name in new[] { "items", "results", "products", "data" })
        {
            if (root.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.Array)
            {
                arr = found;
                return true;
            }
        }
        arr = default;
        return false;
    }

    private static string? GetString(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
                if (v.ValueKind == JsonValueKind.Number) return v.ToString();
            }
        }
        return null;
    }

    private static decimal? GetDecimal(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
            if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), out var s)) return s;
        }
        return null;
    }

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);
}
