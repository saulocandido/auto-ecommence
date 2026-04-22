using System.Text.Json;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;
using AutoCommerce.SupplierSelection.Domain;
using AutoCommerce.SupplierSelection.Evaluation;
using AutoCommerce.SupplierSelection.Services;
using AutoCommerce.SupplierSelection.Tests.Stubs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoCommerce.SupplierSelection.Tests;

public class WorkerTests
{
    [Fact]
    public void ExtractProductId_ParsesIdProperty()
    {
        var id = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new { id, externalId = "ext-1" });
        ProductDiscoveredWorker.ExtractProductId(json).Should().Be(id);
    }

    [Fact]
    public void ExtractProductId_ParsesProductIdProperty()
    {
        var id = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new { productId = id });
        ProductDiscoveredWorker.ExtractProductId(json).Should().Be(id);
    }

    [Fact]
    public void ExtractProductId_NoIdReturnsNull()
    {
        ProductDiscoveredWorker.ExtractProductId("{}").Should().BeNull();
    }

    [Fact]
    public async Task PollOnce_AssignsSupplierForDiscoveredEvents()
    {
        var stub = new StubBrainClient();
        var product = stub.AddProduct("ext-101",
            new SupplierListing("spocket", "s1", 11m, "USD", 5, 4.8, 120, null),
            new SupplierListing("aliexpress", "a1", 7m, "USD", 18, 4.3, 300, null));

        stub.Events.Add(new RecentEventWithPayload(
            Guid.NewGuid(), EventTypes.ProductDiscovered, "brain",
            DateTimeOffset.UtcNow,
            JsonSerializer.Serialize(new { id = product.Id, externalId = product.ExternalId })));

        var services = new ServiceCollection();
        services.AddSingleton<IBrainClient>(stub);
        services.AddSingleton<ISupplierCatalog, StaticSupplierCatalog>();
        services.AddSingleton<ISupplierEvaluator, SupplierEvaluator>();
        services.AddSingleton<ISupplierSelector, SupplierSelector>();
        services.AddSingleton(new SupplierSelectionOptions(40, 21, 10, "IE"));
        services.AddScoped<ISelectionService, SelectionService>();
        services.AddLogging();
        var provider = services.BuildServiceProvider();

        var worker = new ProductDiscoveredWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new DiscoveredWorkerOptions { Enabled = true },
            NullLogger<ProductDiscoveredWorker>.Instance);

        await worker.PollOnceAsync(CancellationToken.None);

        stub.Assignments.Should().HaveCount(1);
        stub.Assignments[0].Id.Should().Be(product.Id);
        stub.Assignments[0].Req.SupplierKey.Should().NotBeNullOrWhiteSpace();
    }
}
