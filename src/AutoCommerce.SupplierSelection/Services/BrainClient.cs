using System.Net.Http.Json;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;

namespace AutoCommerce.SupplierSelection.Services;

public interface IBrainClient
{
    Task<ProductResponse?> GetProductAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<ProductResponse>> ListProductsAsync(string? status, CancellationToken ct);
    Task<ProductResponse?> AssignSupplierAsync(Guid productId, SupplierAssignmentRequest request, CancellationToken ct);
    Task<IReadOnlyList<RecentEventWithPayload>> PollEventsAsync(string type, DateTimeOffset since, int take, CancellationToken ct);
    Task PublishEventAsync(DomainEvent evt, CancellationToken ct);
}

public class BrainClient : IBrainClient
{
    private readonly HttpClient _http;
    private readonly ILogger<BrainClient> _logger;

    public BrainClient(HttpClient http, ILogger<BrainClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<ProductResponse?> GetProductAsync(Guid id, CancellationToken ct)
    {
        var resp = await _http.GetAsync($"api/products/{id}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ProductResponse>(DomainEvent.SerializerOptions, ct);
    }

    public async Task<IReadOnlyList<ProductResponse>> ListProductsAsync(string? status, CancellationToken ct)
    {
        var url = status is null ? "api/products?take=200" : $"api/products?status={Uri.EscapeDataString(status)}&take=200";
        var list = await _http.GetFromJsonAsync<List<ProductResponse>>(url, DomainEvent.SerializerOptions, ct);
        return list ?? new();
    }

    public async Task<ProductResponse?> AssignSupplierAsync(Guid productId, SupplierAssignmentRequest request, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync($"api/products/{productId}/assign-supplier", request, DomainEvent.SerializerOptions, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Brain assign-supplier failed {Status}: {Body}", resp.StatusCode, body);
            return null;
        }
        return await resp.Content.ReadFromJsonAsync<ProductResponse>(DomainEvent.SerializerOptions, ct);
    }

    public async Task<IReadOnlyList<RecentEventWithPayload>> PollEventsAsync(string type, DateTimeOffset since, int take, CancellationToken ct)
    {
        var sinceIso = Uri.EscapeDataString(since.ToString("O"));
        var url = $"api/events?type={Uri.EscapeDataString(type)}&since={sinceIso}&take={take}&includePayload=true";
        var list = await _http.GetFromJsonAsync<List<RecentEventWithPayload>>(url, DomainEvent.SerializerOptions, ct);
        return list ?? new();
    }

    public async Task PublishEventAsync(DomainEvent evt, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync("api/events/publish", evt, DomainEvent.SerializerOptions, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Brain event publish failed {Status}: {Body}", resp.StatusCode, body);
        }
    }
}
