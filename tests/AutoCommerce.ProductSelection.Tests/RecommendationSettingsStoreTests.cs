using AutoCommerce.ProductSelection.Crawlers;
using AutoCommerce.ProductSelection.Services;
using AutoCommerce.Shared.Contracts;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutoCommerce.ProductSelection.Tests;

public class RecommendationSettingsStoreTests
{
    [Fact]
    public async Task UpdateAsync_Persists_Settings_In_Sqlite_And_Returns_Masked_Previews()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"autocommerce-settings-{Guid.NewGuid():N}.db");
        var defaults = new RecommendationRuntimeSettings(
            new OpenAiRecommendationOptions
            {
                ApiKey = "initial-key",
                Model = "gpt-5",
                ReasoningEffort = "low",
                MaxCandidates = 24,
                RequestTimeoutSeconds = 90
            },
            new RecommendationCredentialValues(
                OpenAiApiKey: "initial-key",
                GeminiApiKey: "gemini-initial",
                AdditionalSecrets: new[]
                {
                    new RecommendationNamedCredentialValue("serpapi", "serp-key")
                }),
            new SelectionConfig(new[] { "electronics" }, 10m, 150m, 55, 3, "IE", 18));

        try
        {
            var store = new SqliteRecommendationSettingsStore(
                $"Data Source={databasePath}",
                defaults,
                NullLogger<SqliteRecommendationSettingsStore>.Instance);

            var response = await store.UpdateAsync(
                new RecommendationSettingsUpdate(
                    new RecommendationProviderSettingsUpdate(
                        ApiKey: "updated-key",
                        ClearApiKey: false,
                        Model: "gpt-5-mini",
                        ReasoningEffort: "medium",
                        MaxCandidates: 12,
                        RequestTimeoutSeconds: 45),
                    new RecommendationCredentialsUpdate(
                        OpenAiApiKey: null,
                        ClearOpenAiApiKey: false,
                        GeminiApiKey: "gemini-updated",
                        ClearGeminiApiKey: false,
                        AdditionalSecrets: new[]
                        {
                            new RecommendationNamedCredentialUpdate("serpapi", null, false),
                            new RecommendationNamedCredentialUpdate("tavily", "tavily-key", false)
                        }),
                    new SelectionConfig(new[] { "fitness", " wellness " }, 20m, 200m, 60, 2, "us", 12)),
                default);

            response.Provider.HasApiKey.Should().BeTrue();
            response.Provider.Model.Should().Be("gpt-5-mini");
            response.Provider.EffectiveProvider.Should().Be("OpenAI");
            response.Credentials.HasGeminiApiKey.Should().BeTrue();
            response.Credentials.OpenAiApiKeyPreview.Should().Be("****-key");
            response.Credentials.GeminiApiKeyPreview.Should().Be("****ated");
            response.Credentials.AdditionalSecrets.Select(secret => secret.Name)
                .Should().BeEquivalentTo(new[] { "serpapi", "tavily" });
            response.Credentials.AdditionalSecrets.Should()
                .Contain(secret => secret.Name == "tavily" && secret.Preview == "****-key");
            response.Selection.TargetCategories.Should().BeEquivalentTo(new[] { "fitness", "wellness" });
            response.Selection.TargetMarket.Should().Be("US");

            var reloaded = new SqliteRecommendationSettingsStore(
                $"Data Source={databasePath}",
                defaults,
                NullLogger<SqliteRecommendationSettingsStore>.Instance);

            reloaded.GetCurrent().Provider.ApiKey.Should().Be("updated-key");
            reloaded.GetCurrent().Credentials.GeminiApiKey.Should().Be("gemini-updated");
            reloaded.GetCurrent().Credentials.AdditionalSecrets.Should()
                .Contain(secret => secret.Name == "serpapi" && secret.Value == "serp-key");
            reloaded.GetCurrent().Credentials.AdditionalSecrets.Should()
                .Contain(secret => secret.Name == "tavily" && secret.Value == "tavily-key");
            reloaded.GetCurrent().Selection.TopNPerCategory.Should().Be(2);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }
}
