using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;
using AutoCommerce.SupplierSelection.Domain;
using AutoCommerce.SupplierSelection.Services;
using AutoCommerce.SupplierSelection.Tests.Stubs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoCommerce.SupplierSelection.Tests;

public class FulfillmentServiceTests
{
    private static (FulfillmentService svc, StubBrainClient brain) Build(double? forced = null, int seed = 42)
    {
        var brain = new StubBrainClient();
        var catalog = new StaticSupplierCatalog();
        var opts = new FulfillmentOptions { ForcedSuccessRate = forced, RandomSeed = seed };
        var svc = new FulfillmentService(brain, catalog, opts, NullLogger<FulfillmentService>.Instance);
        return (svc, brain);
    }

    private static FulfillmentRequest Request(string? supplierKey = "amazon-prime") =>
        new(Guid.NewGuid(), "SHOP-001", Guid.NewGuid(), supplierKey, 1, 29.99m, "IE", "buyer@example.com");

    [Fact]
    public async Task Fulfill_NoSupplierKey_Fails()
    {
        var (svc, brain) = Build();
        var result = await svc.FulfillAsync(Request(supplierKey: null), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Reason.Should().Contain("no supplier");
        brain.Published.Should().ContainSingle(e => e.Type == EventTypes.OrderFulfillmentFailed);
    }

    [Fact]
    public async Task Fulfill_ForcedSuccess_AlwaysSucceedsAndEmitsSentEvent()
    {
        var (svc, brain) = Build(forced: 1.0, seed: 123);
        var result = await svc.FulfillAsync(Request(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SupplierOrderId.Should().NotBeNullOrEmpty();
        result.TrackingNumber.Should().StartWith("TRK");
        result.EstimatedDelivery.Should().NotBeNull();
        brain.Published.Should().ContainSingle(e => e.Type == EventTypes.OrderSentToSupplier);
    }

    [Fact]
    public async Task Fulfill_ForcedFailure_ReturnsFailureAndEmitsFailedEvent()
    {
        var (svc, brain) = Build(forced: 0.0, seed: 1);
        var result = await svc.FulfillAsync(Request("aliexpress"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Reason.Should().NotBeNullOrEmpty();
        brain.Published.Should().ContainSingle(e => e.Type == EventTypes.OrderFulfillmentFailed);
    }
}
