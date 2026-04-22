using AutoCommerce.Shared.Contracts;

namespace AutoCommerce.ProductSelection.Scoring;

public interface ICandidateFilter
{
    IReadOnlyList<ScoredCandidate> Filter(IEnumerable<ScoredCandidate> scored, SelectionConfig config);
}

public class TopNPerCategoryFilter : ICandidateFilter
{
    public IReadOnlyList<ScoredCandidate> Filter(IEnumerable<ScoredCandidate> scored, SelectionConfig config)
    {
        return scored
            .Where(s => s.Approved)
            .GroupBy(s => s.Candidate.Category)
            .SelectMany(g => g.OrderByDescending(x => x.Score).Take(Math.Max(1, config.TopNPerCategory)))
            .OrderByDescending(x => x.Score)
            .ToList();
    }
}
