using System.Net;
using System.Net.Http.Json;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;
using FluentAssertions;

namespace AutoCommerce.Brain.Tests;

public class SupplierAssignmentTests : IClassFixture<BrainFactory>
{
    private readonly BrainFactory _factory;
    public SupplierAssignmentTests(BrainFactory f) => _factory = f;

    private static ProductImportDto Sample(string externalId) => new(
        ExternalId: externalId,
        Title: "Assignable Product",
        Category: "electronics",
        Description: null,
        ImageUrls: Array.Empty<string>(),
        Tags: Array.Empty<string>(),
        TargetMarket: "IE",
        Score: 70.0,
        Suppliers: new[]
        {
            new SupplierListing("ali-a", "EXT-A", 14.00m, "USD", 15, 4.1, 200, null),
            new SupplierListing("spocket", "EXT-B", 19.00m, "USD", 4, 4.8, 120, null)
        });

    [Fact]
    public async Task AssignSupplier_UpdatesProduct_AndEmitsEvent()
    {
        var client = _factory.CreateAuthedClient();
        var created = await client.PostAsJsonAsync("/api/products/import",
            Sample("assign:1"), DomainEvent.SerializerOptions);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var product = await created.Content.ReadFromJsonAsync<ProductResponse>(DomainEvent.SerializerOptions);

        var req = new SupplierAssignmentRequest("spocket", 19.00m, 87.5, "USD");
        var resp = await client.PostAsJsonAsync(
            $"/api/products/{product!.Id}/assign-supplier", req, DomainEvent.SerializerOptions);
        resp.EnsureSuccessStatusCode();

        var updated = await resp.Content.ReadFromJsonAsync<ProductResponse>(DomainEvent.SerializerOptions);
        updated!.SupplierKey.Should().Be("spocket");
        updated.Cost.Should().Be(19.00m);
        updated.Price.Should().NotBeNull();

        // event should be queryable with payload
        await Task.Delay(250); // EventRecorder dispatches asynchronously
        var events = await client.GetFromJsonAsync<List<RecentEventWithPayload>>(
            $"/api/events?type={EventTypes.SupplierSelected}&includePayload=true&take=50",
            DomainEvent.SerializerOptions);
        events!.Should().Contain(e => e.PayloadJson.Contains("spocket"));
    }

    [Fact]
    public async Task AssignSupplier_UnknownProduct_Returns404()
    {
        var client = _factory.CreateAuthedClient();
        var req = new SupplierAssignmentRequest("spocket", 19.00m, 87.5, "USD");
        var resp = await client.PostAsJsonAsync(
            $"/api/products/{Guid.NewGuid()}/assign-supplier", req, DomainEvent.SerializerOptions);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EventsQuery_ReturnsRecentProductDiscoveredWithPayload()
    {
        var client = _factory.CreateAuthedClient();
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        var created = await client.PostAsJsonAsync("/api/products/import",
            Sample("assign:events"), DomainEvent.SerializerOptions);
        created.EnsureSuccessStatusCode();

        await Task.Delay(250);
        var encoded = Uri.EscapeDataString(before.ToString("O"));
        var events = await client.GetFromJsonAsync<List<RecentEventWithPayload>>(
            $"/api/events?type={EventTypes.ProductDiscovered}&since={encoded}&includePayload=true",
            DomainEvent.SerializerOptions);
        events!.Should().NotBeEmpty();
        events!.First().PayloadJson.Should().Contain("assign:events");
    }
}
