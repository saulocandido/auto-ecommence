using System.Net;
using System.Text;
using AutoCommerce.ProductSelection.Crawlers;
using AutoCommerce.ProductSelection.Services;
using AutoCommerce.Shared.Contracts;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutoCommerce.ProductSelection.Tests;

public class OpenAiRealtimeTrendSourceTests
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

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(string ResponseBody, HttpStatusCode StatusCode)> _responses;

        public StubHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
            : this(new[] { (responseBody, statusCode) })
        {
        }

        public StubHandler(IEnumerable<(string ResponseBody, HttpStatusCode StatusCode)> responses)
        {
            _responses = new Queue<(string ResponseBody, HttpStatusCode StatusCode)>(responses);
        }

        public string? LastRequestBody { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public string? LastAuthorizationHeader { get; private set; }
        public string? LastGoogleApiKey { get; private set; }
        public List<Uri?> RequestUris { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            RequestUris.Add(request.RequestUri);
            LastAuthorizationHeader = request.Headers.Authorization?.ToString();
            LastGoogleApiKey = request.Headers.TryGetValues("x-goog-api-key", out var values)
                ? values.SingleOrDefault()
                : null;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var response = _responses.Count > 0
                ? _responses.Dequeue()
                : throw new InvalidOperationException("No stub response remaining for this request.");

            return new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.ResponseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task FetchAsync_Uses_OpenAi_WebSearch_And_Maps_Candidates()
    {
        const string openAiResponse = """
            {
              "output": [
                {
                  "type": "message",
                  "role": "assistant",
                  "content": [
                    {
                      "type": "output_text",
                      "text": "{\"candidates\":[{\"externalId\":\"trend:portable-projector\",\"title\":\"Portable Smart Mini Projector\",\"category\":\"electronics\",\"description\":\"Compact smart projector with strong current search and social demand.\",\"imageUrls\":[\"https://images.example.com/projector.jpg\"],\"tags\":[\"projector\",\"smart-home\",\"viral\"],\"price\":129.99,\"currency\":\"USD\",\"reviewCount\":1820,\"rating\":4.6,\"estimatedMonthlySearches\":22000,\"competitorCount\":240,\"shippingDaysToTarget\":9,\"supplierCandidates\":[{\"supplierKey\":\"aliexpress\",\"externalProductId\":\"proj-001\",\"cost\":54.5,\"currency\":\"USD\",\"shippingDays\":9,\"rating\":4.7,\"stockAvailable\":850,\"url\":\"https://supplier.example.com/proj-001\"}]}]}"
                    }
                  ]
                }
              ]
            }
            """;

        var handler = new StubHandler(openAiResponse);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        var config = new SelectionConfig(new[] { "electronics" }, 10m, 150m, 55, 3, "IE", 18);
        var source = new OpenAiRealtimeTrendSource(
            httpClient,
            new StubSettingsStore(new RecommendationRuntimeSettings(
                new OpenAiRecommendationOptions
                {
                    ApiKey = "test-key",
                    Model = "gpt-5",
                    ReasoningEffort = "low",
                    MaxCandidates = 24,
                    RequestTimeoutSeconds = 90
                },
                new RecommendationCredentialValues(
                    OpenAiApiKey: "test-key",
                    GeminiApiKey: "gemini-key",
                    AdditionalSecrets: Array.Empty<RecommendationNamedCredentialValue>()),
                config)),
            NullLogger<OpenAiRealtimeTrendSource>.Instance);

        var items = await source.FetchAsync(config, default);

        items.Should().ContainSingle();
        items[0].Title.Should().Be("Portable Smart Mini Projector");
        items[0].Source.Should().Be("OpenAIRealtimeWebSearch");
        items[0].SupplierCandidates.Should().ContainSingle();
        handler.LastRequestBody.Should().Contain("\"type\":\"web_search\"");
        handler.LastRequestBody.Should().Contain("\"model\":\"gpt-5\"");
        handler.LastRequestBody.Should().Contain("\"country\":\"IE\"");
        handler.LastAuthorizationHeader.Should().Be("Bearer test-key");
        handler.LastGoogleApiKey.Should().BeNull();
    }

    [Fact]
    public async Task FetchAsync_Falls_Back_To_Gemini_When_OpenAi_Key_Is_Missing()
    {
        const string geminiResponse = """
            {
              "candidates": [
                {
                  "content": {
                    "role": "model",
                    "parts": [
                        {
                        "text": "```json\n{\"candidates\":[{\"externalId\":\"trend:walking-pad\",\"title\":\"Compact Walking Pad\",\"category\":\"fitness\",\"description\":\"Home walking pad with strong current search demand.\",\"imageUrls\":[\"https://images.example.com/walking-pad.jpg\"],\"tags\":[\"fitness\",\"home\",\"walking\"],\"price\":189.99,\"currency\":\"USD\",\"reviewCount\":950,\"rating\":4.4,\"estimatedMonthlySearches\":18000,\"competitorCount\":140,\"shippingDaysToTarget\":8,\"supplierCandidates\":[{\"supplierKey\":\"supplier\",\"externalProductId\":\"walk-001\",\"cost\":88.0,\"currency\":\"USD\",\"shippingDays\":8,\"rating\":4.5,\"stockAvailable\":420,\"url\":\"https://supplier.example.com/walk-001\"}]}]}\n```"
                      }
                    ]
                  }
                }
              ]
            }
            """;

        var handler = new StubHandler(geminiResponse);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        var config = new SelectionConfig(new[] { "fitness" }, 10m, 250m, 55, 3, "IE", 18);
        var source = new OpenAiRealtimeTrendSource(
            httpClient,
            new StubSettingsStore(new RecommendationRuntimeSettings(
                new OpenAiRecommendationOptions
                {
                    ApiKey = null,
                    Model = "gpt-5",
                    ReasoningEffort = "low",
                    MaxCandidates = 24,
                    RequestTimeoutSeconds = 90
                },
                new RecommendationCredentialValues(
                    OpenAiApiKey: null,
                    GeminiApiKey: "gemini-key",
                    AdditionalSecrets: Array.Empty<RecommendationNamedCredentialValue>()),
                config)),
            NullLogger<OpenAiRealtimeTrendSource>.Instance);

        var items = await source.FetchAsync(config, default);

        items.Should().ContainSingle();
        items[0].Title.Should().Be("Compact Walking Pad");
        items[0].Source.Should().Be("GeminiRealtimeGoogleSearch");
        handler.LastAuthorizationHeader.Should().BeNull();
        handler.LastGoogleApiKey.Should().Be("gemini-key");
        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.AbsoluteUri.Should().Contain("generativelanguage.googleapis.com");
        handler.LastRequestUri.AbsoluteUri.Should().Contain("gemini-2.5-flash:generateContent");
        handler.LastRequestBody.Should().Contain("\"google_search\":{}");
    }

    [Fact]
    public async Task FetchAsync_Falls_Back_To_Gemini_Flash_Lite_When_Flash_Is_Unavailable()
    {
        const string unavailableResponse = """
            {
              "error": {
                "code": 503,
                "message": "This model is currently experiencing high demand.",
                "status": "UNAVAILABLE"
              }
            }
            """;

        const string geminiResponse = """
            {
              "candidates": [
                {
                  "content": {
                    "role": "model",
                    "parts": [
                      {
                        "text": "{\"candidates\":[{\"externalId\":\"trend:desk-lamp\",\"title\":\"Rechargeable Desk Lamp\",\"category\":\"home\",\"description\":\"Portable desk lamp with strong search demand.\",\"imageUrls\":[],\"tags\":[\"home\",\"lighting\"],\"price\":39.99,\"currency\":\"USD\",\"reviewCount\":540,\"rating\":4.5,\"estimatedMonthlySearches\":12000,\"competitorCount\":90,\"shippingDaysToTarget\":7,\"supplierCandidates\":[{\"supplierKey\":\"supplier\",\"externalProductId\":\"lamp-001\",\"cost\":14.0,\"currency\":\"USD\",\"shippingDays\":7,\"rating\":4.5,\"stockAvailable\":300,\"url\":\"https://supplier.example.com/lamp-001\"}]}]}"
                      }
                    ]
                  }
                }
              ]
            }
            """;

        var handler = new StubHandler(new[]
        {
            (unavailableResponse, HttpStatusCode.ServiceUnavailable),
            (geminiResponse, HttpStatusCode.OK)
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        var config = new SelectionConfig(new[] { "home" }, 10m, 60m, 55, 3, "IE", 18);
        var source = new OpenAiRealtimeTrendSource(
            httpClient,
            new StubSettingsStore(new RecommendationRuntimeSettings(
                new OpenAiRecommendationOptions
                {
                    ApiKey = null,
                    Model = "gpt-5",
                    ReasoningEffort = "low",
                    MaxCandidates = 24,
                    RequestTimeoutSeconds = 90
                },
                new RecommendationCredentialValues(
                    OpenAiApiKey: null,
                    GeminiApiKey: "gemini-key",
                    AdditionalSecrets: Array.Empty<RecommendationNamedCredentialValue>()),
                config)),
            NullLogger<OpenAiRealtimeTrendSource>.Instance);

        var items = await source.FetchAsync(config, default);

        items.Should().ContainSingle();
        handler.RequestUris.Should().HaveCount(2);
        handler.RequestUris[0]!.AbsoluteUri.Should().Contain("gemini-2.5-flash:generateContent");
        handler.RequestUris[1]!.AbsoluteUri.Should().Contain("gemini-2.5-flash-lite:generateContent");
    }
}
