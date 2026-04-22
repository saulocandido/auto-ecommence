using System.Net.Http.Json;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;

namespace AutoCommerce.ProductSelection.Services;

public interface IBrainClient
{
    Task<ProductResponse?> ImportProductAsync(ProductImportDto dto, CancellationToken ct);
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

    public async Task<ProductResponse?> ImportProductAsync(ProductImportDto dto, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync("api/products/import", dto, DomainEvent.SerializerOptions, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Brain import failed {Status}: {Body}", resp.StatusCode, body);
            return null;
        }
        return await resp.Content.ReadFromJsonAsync<ProductResponse>(DomainEvent.SerializerOptions, ct);
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
