using AutoCommerce.ProductSelection.Crawlers;
using AutoCommerce.ProductSelection.Scoring;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;

namespace AutoCommerce.ProductSelection.Services;

public interface ISelectionOrchestrator
{
    Task<RecommendationResponse> GenerateAsync(SelectionConfig? configOverride, CancellationToken ct);
    Task<(int Imported, int Total, int Approved)> DiscoverAndImportAsync(SelectionConfig? configOverride, CancellationToken ct);
}

public class SelectionOrchestrator : ISelectionOrchestrator
{
    private readonly IEnumerable<ICandidateSource> _sources;
    private readonly IScoringEngine _scoring;
    private readonly ICandidateFilter _filter;
    private readonly IBrainClient _brain;
    private readonly IRecommendationSettingsStore _settingsStore;
    private readonly IRecommendationCache _cache;
    private readonly ILogger<SelectionOrchestrator> _logger;

    public SelectionOrchestrator(
        IEnumerable<ICandidateSource> sources,
        IScoringEngine scoring,
        ICandidateFilter filter,
        IBrainClient brain,
        IRecommendationSettingsStore settingsStore,
        IRecommendationCache cache,
        ILogger<SelectionOrchestrator> logger)
    {
        _sources = sources;
        _scoring = scoring;
        _filter = filter;
        _brain = brain;
        _settingsStore = settingsStore;
        _cache = cache;
        _logger = logger;
    }

    public async Task<RecommendationResponse> GenerateAsync(SelectionConfig? configOverride, CancellationToken ct)
    {
        // If no override config, try returning cached scan results first
        if (configOverride is null)
        {
            var cached = await _cache.GetLatestAsync(ct);
            if (cached is not null)
            {
                _logger.LogInformation("Returning {Count} cached recommendations", cached.Recommendations.Count);
                return cached;
            }
        }

        var config = configOverride ?? _settingsStore.GetCurrent().Selection;
        var scored = await ScoreAllAsync(config, ct);
        var approved = _filter.Filter(scored, config);

        var byId = approved.ToDictionary(x => x.Candidate.ExternalId, x => x);
        var merged = scored.Select(s => byId.ContainsKey(s.Candidate.ExternalId)
            ? s with { Approved = true, RejectionReason = null }
            : s).ToList();

        var response = new RecommendationResponse(DateTimeOffset.UtcNow, config, merged);
        await _cache.SaveAsync(response, ct);
        return response;
    }

    public async Task<(int Imported, int Total, int Approved)> DiscoverAndImportAsync(SelectionConfig? configOverride, CancellationToken ct)
    {
        var config = configOverride ?? _settingsStore.GetCurrent().Selection;
        var scored = await ScoreAllAsync(config, ct);
        var approved = _filter.Filter(scored, config);
        int imported = 0;

        foreach (var candidate in approved)
        {
            ct.ThrowIfCancellationRequested();
            var dto = ToImportDto(candidate, config);
            var result = await _brain.ImportProductAsync(dto, ct);
            if (result is null)
            {
                _logger.LogWarning("Failed to import {Id}", dto.ExternalId);
                continue;
            }
            imported++;
            await _brain.PublishEventAsync(DomainEvent.Create(EventTypes.ProductDiscovered, "product-selection",
                new { result.Id, result.ExternalId, result.Title, candidate.Score }), ct);
        }

        _logger.LogInformation("Imported {Count} products from {Total} candidates", imported, scored.Count);

        // Cache the full results so GET /recommendations returns them
        var byId = approved.ToDictionary(x => x.Candidate.ExternalId, x => x);
        var merged = scored.Select(s => byId.ContainsKey(s.Candidate.ExternalId)
            ? s with { Approved = true, RejectionReason = null }
            : s).ToList();
        await _cache.SaveAsync(new RecommendationResponse(DateTimeOffset.UtcNow, config, merged), ct);

        return (imported, scored.Count, approved.Count);
    }

    private async Task<List<ScoredCandidate>> ScoreAllAsync(SelectionConfig config, CancellationToken ct)
    {
        var all = new List<ProductCandidate>();
        foreach (var src in _sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var fetched = await src.FetchAsync(config, ct);
                all.AddRange(fetched);
                _logger.LogInformation("Fetched {Count} candidates from {Source}", fetched.Count, src.SourceName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Source {Source} failed", src.SourceName);
            }
        }
        return all.Select(c => _scoring.Score(c, config)).ToList();
    }

    internal static ProductImportDto ToImportDto(ScoredCandidate scored, SelectionConfig config)
    {
        var c = scored.Candidate;
        return new ProductImportDto(
            ExternalId: c.ExternalId,
            Title: c.Title,
            Category: c.Category,
            Description: c.Description,
            ImageUrls: c.ImageUrls,
            Tags: c.Tags,
            TargetMarket: config.TargetMarket,
            Score: scored.Score,
            Suppliers: c.SupplierCandidates,
            ScoreBreakdown: scored.Breakdown);
    }
}
