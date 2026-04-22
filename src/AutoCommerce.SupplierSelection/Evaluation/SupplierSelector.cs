using AutoCommerce.Shared.Contracts;

namespace AutoCommerce.SupplierSelection.Evaluation;

public interface ISupplierSelector
{
    SupplierSelectionResult Select(
        Guid productId,
        string externalId,
        IReadOnlyList<SupplierListing> listings,
        SupplierSelectionOptions options);
}

public class SupplierSelector : ISupplierSelector
{
    private readonly ISupplierEvaluator _evaluator;

    public SupplierSelector(ISupplierEvaluator evaluator) => _evaluator = evaluator;

    public SupplierSelectionResult Select(
        Guid productId,
        string externalId,
        IReadOnlyList<SupplierListing> listings,
        SupplierSelectionOptions options)
    {
        if (listings.Count == 0)
        {
            return new SupplierSelectionResult(
                productId, externalId, null, null, null, null,
                Array.Empty<SupplierEvaluation>(), "no supplier listings");
        }

        var evals = _evaluator.Evaluate(listings, options);
        var ordered = evals.OrderByDescending(e => e.Score).ToList();
        var winner = ordered.FirstOrDefault(e => e.Viable);

        if (winner is null)
        {
            var reason = ordered.First().RejectionReason ?? "no viable supplier";
            return new SupplierSelectionResult(
                productId, externalId, null, null, null, null, ordered, reason);
        }

        return new SupplierSelectionResult(
            productId, externalId,
            winner.SupplierKey, winner.Cost, winner.Currency, winner.Score,
            ordered, null);
    }
}
