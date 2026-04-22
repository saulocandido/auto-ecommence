using AutoCommerce.Shared.Contracts;
using AutoCommerce.SupplierSelection.Evaluation;

namespace AutoCommerce.SupplierSelection.Services;

public interface ISelectionService
{
    SupplierSelectionResult Preview(ProductResponse product, SupplierSelectionOptions? overrideOptions = null);
    Task<SupplierSelectionResult> SelectAndAssignAsync(Guid productId, CancellationToken ct);
}

public class SelectionService : ISelectionService
{
    private readonly IBrainClient _brain;
    private readonly ISupplierSelector _selector;
    private readonly SupplierSelectionOptions _defaults;
    private readonly ILogger<SelectionService> _logger;

    public SelectionService(
        IBrainClient brain,
        ISupplierSelector selector,
        SupplierSelectionOptions defaults,
        ILogger<SelectionService> logger)
    {
        _brain = brain;
        _selector = selector;
        _defaults = defaults;
        _logger = logger;
    }

    public SupplierSelectionResult Preview(ProductResponse product, SupplierSelectionOptions? overrideOptions = null)
    {
        var opts = overrideOptions ?? _defaults;
        return _selector.Select(product.Id, product.ExternalId, product.Suppliers, opts);
    }

    public async Task<SupplierSelectionResult> SelectAndAssignAsync(Guid productId, CancellationToken ct)
    {
        var product = await _brain.GetProductAsync(productId, ct)
                      ?? throw new InvalidOperationException($"Product {productId} not found in Brain");

        var result = Preview(product);

        if (result.ChosenSupplierKey is null)
        {
            _logger.LogInformation("No viable supplier for product {ExternalId}: {Reason}",
                product.ExternalId, result.RejectionReason);
            return result;
        }

        var assignment = new SupplierAssignmentRequest(
            result.ChosenSupplierKey,
            result.ChosenCost!.Value,
            result.Score!.Value,
            result.Currency);

        var updated = await _brain.AssignSupplierAsync(productId, assignment, ct);
        if (updated is null)
            _logger.LogWarning("Brain rejected supplier assignment for {ExternalId}", product.ExternalId);
        else
            _logger.LogInformation("Assigned {Supplier} to {ExternalId} (score {Score})",
                result.ChosenSupplierKey, product.ExternalId, result.Score);

        return result;
    }
}
