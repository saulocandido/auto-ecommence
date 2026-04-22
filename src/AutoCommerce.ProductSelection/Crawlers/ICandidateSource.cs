using AutoCommerce.Shared.Contracts;

namespace AutoCommerce.ProductSelection.Crawlers;

public interface ICandidateSource
{
    string SourceName { get; }
    Task<IReadOnlyList<ProductCandidate>> FetchAsync(SelectionConfig config, CancellationToken ct);
}
