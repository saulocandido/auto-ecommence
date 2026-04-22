using AutoCommerce.ProductSelection.Crawlers;
using AutoCommerce.ProductSelection.Scoring;
using AutoCommerce.ProductSelection.Services;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutoCommerce.ProductSelection.Tests;

public class OrchestratorTests
{
    private sealed class StubSettingsStore : IRecommendationSettingsStore
    {
        private readonly RecommendationRuntimeSettings _settings;

        public StubSettingsStore(RecommendationRuntimeSettings settings) => _settings = settings;

        public RecommendationRuntimeSettings GetCurrent() => _settings;

        public RecommendationSettingsResponse GetResponse()
            => new(
                new RecommendationProviderSettings(
                    HasApiKey: !string.IsNullOrWhiteSpace(_settings.Credentials.OpenAiApiKey),
                    Model: _settings.Provider.Model,
                    ReasoningEffort: _settings.Provider.ReasoningEffort,
                    MaxCandidates: _settings.Provider.MaxCandidates,
                    RequestTimeoutSeconds: _settings.Provider.RequestTimeoutSeconds,
                    EffectiveProvider: "None"),
                new RecommendationCredentialsSettings(
                    HasOpenAiApiKey: !string.IsNullOrWhiteSpace(_settings.Credentials.OpenAiApiKey),
                    HasGeminiApiKey: !string.IsNullOrWhiteSpace(_settings.Credentials.GeminiApiKey),
                    OpenAiApiKeyPreview: null,
                    GeminiApiKeyPreview: null,
                    AdditionalSecrets: _settings.Credentials.AdditionalSecrets
                        .Select(secret => new RecommendationNamedCredentialStatus(secret.Name, true, null))
                        .ToArray()),
                _settings.Selection);

        public Task<RecommendationSettingsResponse> UpdateAsync(RecommendationSettingsUpdate update, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class StubSource : ICandidateSource
    {
        private readonly IReadOnlyList<ProductCandidate> _items;
        public StubSource(string name, IReadOnlyList<ProductCandidate> items) { SourceName = name; _items = items; }
        public string SourceName { get; }
        public Task<IReadOnlyList<ProductCandidate>> FetchAsync(SelectionConfig c, CancellationToken ct) => Task.FromResult(_items);
    }

    private sealed class RecordingBrainClient : IBrainClient
    {
        public List<ProductImportDto> Imported { get; } = new();
        public List<DomainEvent> Events { get; } = new();
        public Task<ProductResponse?> ImportProductAsync(ProductImportDto dto, CancellationToken ct)
        {
            Imported.Add(dto);
            var cost = dto.Suppliers.Min(s => s.Cost);
            return Task.FromResult<ProductResponse?>(new ProductResponse(
                Guid.NewGuid(), dto.ExternalId, dto.Title, dto.Category, dto.Description,
                dto.ImageUrls, dto.Tags, dto.TargetMarket, dto.Score,
                cost, cost * 2, 50.0, "Active", dto.Suppliers[0].SupplierKey,
                dto.Suppliers, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
        public Task PublishEventAsync(DomainEvent evt, CancellationToken ct)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryCache : IRecommendationCache
    {
        public RecommendationResponse? Saved { get; private set; }
        public Task SaveAsync(RecommendationResponse response, CancellationToken ct) { Saved = response; return Task.CompletedTask; }
        public Task<RecommendationResponse?> GetLatestAsync(CancellationToken ct) => Task.FromResult(Saved);
    }

    private static ProductCandidate Candidate(string id, string category = "electronics", decimal price = 50m) => new(
        ExternalId: id, Source: "stub", Title: id, Category: category, Description: null,
        ImageUrls: Array.Empty<string>(), Tags: Array.Empty<string>(),
        Price: price, Currency: "USD",
        ReviewCount: 2000, Rating: 4.6, EstimatedMonthlySearches: 20_000,
        CompetitorCount: 200, ShippingDaysToTarget: 10,
        SupplierCandidates: new[] { new SupplierListing("s", "x", 15m, "USD", 10, 4.5, 100, null) });

    [Fact]
    public async Task Generate_Returns_All_Candidates_With_Approval_Flags()
    {
        var source = new StubSource("stub", new[]
        {
            Candidate("a"), Candidate("b", price: 2m), Candidate("c", category: "toys")
        });
        var cfg = new SelectionConfig(new[] { "electronics" }, 5m, 500m, 40, 5, "IE", 20);
        var orch = new SelectionOrchestrator(
            new[] { source }, new ScoringEngine(), new TopNPerCategoryFilter(),
            new RecordingBrainClient(),
            new StubSettingsStore(new RecommendationRuntimeSettings(
                new OpenAiRecommendationOptions(),
                new RecommendationCredentialValues(null, null, Array.Empty<RecommendationNamedCredentialValue>()),
                cfg)),
            new InMemoryCache(),
            NullLogger<SelectionOrchestrator>.Instance);

        var result = await orch.GenerateAsync(cfg, default);
        result.Recommendations.Should().HaveCount(3);
        result.Recommendations.Count(r => r.Approved).Should().Be(1);
        result.Recommendations.Single(r => r.Approved).Candidate.ExternalId.Should().Be("a");
    }

    [Fact]
    public async Task DiscoverAndImport_Calls_BrainClient_For_Approved_Only()
    {
        var source = new StubSource("stub", new[]
        {
            Candidate("ok-1"), Candidate("ok-2"),
            Candidate("bad", category: "toys")
        });
        var brain = new RecordingBrainClient();
        var cfg = new SelectionConfig(new[] { "electronics" }, 5m, 500m, 40, 5, "IE", 20);
        var orch = new SelectionOrchestrator(
            new[] { source }, new ScoringEngine(), new TopNPerCategoryFilter(),
            brain,
            new StubSettingsStore(new RecommendationRuntimeSettings(
                new OpenAiRecommendationOptions(),
                new RecommendationCredentialValues(null, null, Array.Empty<RecommendationNamedCredentialValue>()),
                cfg)),
            new InMemoryCache(),
            NullLogger<SelectionOrchestrator>.Instance);

        var (imported, total, approved) = await orch.DiscoverAndImportAsync(null, default);
        imported.Should().Be(2);
        brain.Imported.Select(p => p.ExternalId).Should().BeEquivalentTo(new[] { "ok-1", "ok-2" });
        brain.Events.Should().AllSatisfy(e => e.Type.Should().Be(EventTypes.ProductDiscovered));
    }

}
