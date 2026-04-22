using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;

namespace AutoCommerce.StoreManagement.Services;

public interface IBrainClient
{
    Task<ProductResponse?> GetProductAsync(Guid productId, CancellationToken ct = default);
    Task<List<ProductResponse>> GetProductsAsync(string? status, CancellationToken ct = default);
    Task PublishEventAsync(DomainEvent evt, CancellationToken ct = default);
    Task<List<RecentEventWithPayload>> PollEventsAsync(string type, DateTimeOffset since, int take, CancellationToken ct = default);
}

public class BrainClient : IBrainClient
{
    private readonly HttpClient _client;
    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public BrainClient(HttpClient client)
    {
        _client = client;
    }

    public async Task<ProductResponse?> GetProductAsync(Guid productId, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetAsync($"/api/products/{productId}", ct);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(ct);
            return System.Text.Json.JsonSerializer.Deserialize<ProductResponse>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<ProductResponse>> GetProductsAsync(string? status, CancellationToken ct = default)
    {
        var path = string.IsNullOrWhiteSpace(status)
            ? "/api/products"
            : $"/api/products?status={Uri.EscapeDataString(status)}";
        var response = await _client.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return System.Text.Json.JsonSerializer.Deserialize<List<ProductResponse>>(json, JsonOpts)
            ?? new List<ProductResponse>();
    }

    public async Task PublishEventAsync(DomainEvent evt, CancellationToken ct = default)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(evt);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        await _client.PostAsync("/api/events/publish", content, ct);
    }

    public async Task<List<RecentEventWithPayload>> PollEventsAsync(string type, DateTimeOffset since, int take, CancellationToken ct = default)
    {
        var iso = Uri.EscapeDataString(since.ToUniversalTime().ToString("o"));
        var path = $"/api/events?type={Uri.EscapeDataString(type)}&since={iso}&take={take}&includePayload=true";
        try
        {
            var response = await _client.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode) return new List<RecentEventWithPayload>();
            var json = await response.Content.ReadAsStringAsync(ct);
            return System.Text.Json.JsonSerializer.Deserialize<List<RecentEventWithPayload>>(json, JsonOpts)
                ?? new List<RecentEventWithPayload>();
        }
        catch
        {
            return new List<RecentEventWithPayload>();
        }
    }
}
