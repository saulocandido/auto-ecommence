using AutoCommerce.Shared.Contracts;
using AutoCommerce.SupplierSelection.Domain;
using AutoCommerce.SupplierSelection.Evaluation;
using AutoCommerce.SupplierSelection.Services;
using AutoCommerce.SupplierSelection.Tests.Stubs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoCommerce.SupplierSelection.Tests;

public class SelectionServiceTests
{
    private static (SelectionService svc, StubBrainClient stub) Build()
    {
        var stub = new StubBrainClient();
        var catalog = new StaticSupplierCatalog();
        var evaluator = new SupplierEvaluator(catalog);
        var selector = new SupplierSelector(evaluator);
        var defaults = new SupplierSelectionOptions(40, 21, 10, "IE");
        var svc = new SelectionService(stub, selector, defaults, NullLogger<SelectionService>.Instance);
        return (svc, stub);
    }

    [Fact]
    public async Task SelectAndAssign_ViableProduct_CallsBrainAssignment()
    {
        var (svc, stub) = Build();
        var product = stub.AddProduct("ext-1",
            new SupplierListing("aliexpress",   "a1", 9m,  "USD", 18, 4.3, 200, null),
            new SupplierListing("amazon-prime", "am1", 15m, "USD",  3, 4.9, 500, null));

        var result = await svc.SelectAndAssignAsync(product.Id, CancellationToken.None);

        result.ChosenSupplierKey.Should().Be("amazon-prime");
        stub.Assignments.Should().HaveCount(1);
        stub.Assignments[0].Id.Should().Be(product.Id);
        stub.Assignments[0].Req.SupplierKey.Should().Be("amazon-prime");
        stub.Assignments[0].Req.Cost.Should().Be(15m);
    }

    [Fact]
    public async Task SelectAndAssign_NoViableSuppliers_DoesNotCallAssign()
    {
        var (svc, stub) = Build();
        var product = stub.AddProduct("ext-2",
            new SupplierListing("aliexpress", "a1", 9m, "USD", 40, 4.3, 2, null));

        var result = await svc.SelectAndAssignAsync(product.Id, CancellationToken.None);

        result.ChosenSupplierKey.Should().BeNull();
        stub.Assignments.Should().BeEmpty();
    }

    [Fact]
    public async Task SelectAndAssign_UnknownProduct_Throws()
    {
        var (svc, _) = Build();
        await FluentActions
            .Awaiting(() => svc.SelectAndAssignAsync(Guid.NewGuid(), CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
    }
}
