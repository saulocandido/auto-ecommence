using System.Net.Http.Json;
using System.Text.Json;
using AutoCommerce.ProductSelection.Crawlers;
using AutoCommerce.Shared.Contracts;

namespace AutoCommerce.ProductSelection.Services;

/// <summary>
/// Fetches settings from the Configuration microservice and caches locally.
/// Implements IRecommendationSettingsStore so nothing else in ProductSelection needs to change.
/// </summary>
public sealed class RemoteConfigurationClient : IRecommendationSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<RemoteConfigurationClient> _logger;
    private readonly object _sync = new();
    private RecommendationRuntimeSettings _cached;

    public RemoteConfigurationClient(
        HttpClient http,
        RecommendationRuntimeSettings fallback,
        ILogger<RemoteConfigurationClient> logger)
    {
        _http = http;
        _logger = logger;
        _cached = fallback;

        // Eagerly fetch on construction (fire-and-forget; if it fails we use fallback)
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "Initial config fetch failed, using fallback"); }
        });
    }

    public RecommendationRuntimeSettings GetCurrent()
    {
        lock (_sync) return _cached;
    }

    public RecommendationSettingsResponse GetResponse()
    {
        // The dashboard no longer calls ProductSelection for config — this is just for completeness.
        var c = GetCurrent();
        return new RecommendationSettingsResponse(
            new RecommendationProviderSettings(
                HasApiKey: !string.IsNullOrWhiteSpace(c.Credentials.OpenAiApiKey),
                Model: c.Provider.Model,
                ReasoningEffort: c.Provider.ReasoningEffort,
                MaxCandidates: c.Provider.MaxCandidates,
                RequestTimeoutSeconds: c.Provider.RequestTimeoutSeconds,
                EffectiveProvider: "Remote"),
            new RecommendationCredentialsSettings(
                HasOpenAiApiKey: !string.IsNullOrWhiteSpace(c.Credentials.OpenAiApiKey),
                HasGeminiApiKey: !string.IsNullOrWhiteSpace(c.Credentials.GeminiApiKey),
                OpenAiApiKeyPreview: null,
                GeminiApiKeyPreview: null,
                AdditionalSecrets: Array.Empty<RecommendationNamedCredentialStatus>()),
            c.Selection);
    }

    public Task<RecommendationSettingsResponse> UpdateAsync(RecommendationSettingsUpdate update, CancellationToken ct)
    {
        // Updates go through the Configuration MS directly, not through ProductSelection.
        throw new NotSupportedException("Updates should be sent to the Configuration microservice directly.");
    }

    /// <summary>Called by the /configuration/sync webhook endpoint when the Configuration MS pushes changes.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetFromJsonAsync<InternalSettingsDto>("/configuration/recommendations/internal", JsonOpts, ct);
            if (resp is null)
            {
                _logger.LogWarning("Configuration MS returned null settings");
                return;
            }

            var settings = new RecommendationRuntimeSettings(
                new OpenAiRecommendationOptions
                {
                    ApiKey = resp.Provider?.ApiKey,
                    Model = resp.Provider?.Model ?? "gpt-5",
                    ReasoningEffort = resp.Provider?.ReasoningEffort ?? "low",
                    MaxCandidates = resp.Provider?.MaxCandidates ?? 48,
                    RequestTimeoutSeconds = resp.Provider?.RequestTimeoutSeconds ?? 90
                },
                new RecommendationCredentialValues(
                    resp.Credentials?.OpenAiApiKey,
                    resp.Credentials?.GeminiApiKey,
                    (resp.Credentials?.AdditionalSecrets ?? Array.Empty<NamedCred>())
                        .Select(s => new RecommendationNamedCredentialValue(s.Name, s.Value))
                        .ToArray()),
                resp.Selection ?? new SelectionConfig(
                    Array.Empty<string>(), null, null, null, 3, "IE", null));

            lock (_sync) _cached = settings;
            _logger.LogInformation("Refreshed configuration from Configuration MS (model={Model})", settings.Provider.Model);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh configuration from Configuration MS");
            throw;
        }
    }

    // ── JSON mapping DTOs ──

    private sealed record InternalSettingsDto(
        ProviderDto? Provider,
        CredentialsDto? Credentials,
        SelectionConfig? Selection);

    private sealed record ProviderDto(
        string? ApiKey,
        string? Model,
        string? ReasoningEffort,
        int? MaxCandidates,
        int? RequestTimeoutSeconds);

    private sealed record CredentialsDto(
        string? OpenAiApiKey,
        string? GeminiApiKey,
        NamedCred[]? AdditionalSecrets);

    private sealed record NamedCred(string Name, string Value);
}
