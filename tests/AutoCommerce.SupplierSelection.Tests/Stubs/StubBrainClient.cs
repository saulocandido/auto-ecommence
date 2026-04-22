using System.Collections.Concurrent;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;
using AutoCommerce.SupplierSelection.Services;

namespace AutoCommerce.SupplierSelection.Tests.Stubs;

public class StubBrainClient : IBrainClient
{
    public ConcurrentDictionary<Guid, ProductResponse> Products { get; } = new();
    public List<RecentEventWithPayload> Events { get; } = new();
    public List<(Guid Id, SupplierAssignmentRequest Req)> Assignments { get; } = new();
    public List<DomainEvent> Published { get; } = new();

    public Task<ProductResponse?> GetProductAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Products.TryGetValue(id, out var p) ? p : null);

    public Task<IReadOnlyList<ProductResponse>> ListProductsAsync(string? status, CancellationToken ct)
    {
        IEnumerable<ProductResponse> q = Products.Values;
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(p => string.Equals(p.Status, status, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyList<ProductResponse>>(q.ToList());
    }

    public Task<ProductResponse?> AssignSupplierAsync(Guid productId, SupplierAssignmentRequest request, CancellationToken ct)
    {
        Assignments.Add((productId, request));
        if (!Products.TryGetValue(productId, out var existing)) return Task.FromResult<ProductResponse?>(null);
        var updated = existing with { SupplierKey = request.SupplierKey, Cost = request.Cost };
        Products[productId] = updated;
        return Task.FromResult<ProductResponse?>(updated);
    }

    public Task<IReadOnlyList<RecentEventWithPayload>> PollEventsAsync(string type, DateTimeOffset since, int take, CancellationToken ct)
    {
        var list = Events
            .Where(e => e.Type == type && e.OccurredAt > since)
            .OrderBy(e => e.OccurredAt)
            .Take(take)
            .ToList();
        return Task.FromResult<IReadOnlyList<RecentEventWithPayload>>(list);
    }

    public Task PublishEventAsync(DomainEvent evt, CancellationToken ct)
    {
        Published.Add(evt);
        return Task.CompletedTask;
    }

    public ProductResponse AddProduct(string externalId, params SupplierListing[] suppliers)
    {
        var id = Guid.NewGuid();
        var product = new ProductResponse(
            id, externalId, $"Title {externalId}", "electronics", null,
            Array.Empty<string>(), Array.Empty<string>(), "IE", 82.0,
            null, null, null, "Draft", null, suppliers,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Products[id] = product;
        return product;
    }
}
