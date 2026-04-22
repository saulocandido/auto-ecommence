using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoCommerce.ProductSelection.Services;
using AutoCommerce.Shared.Contracts;

namespace AutoCommerce.ProductSelection.Crawlers;

public sealed class OpenAiRecommendationOptions
{
    public string? ApiKey { get; init; }
    public string Model { get; init; } = "gpt-5";
    public string ReasoningEffort { get; init; } = "low";
    public int MaxCandidates { get; init; } = 48;
    public int RequestTimeoutSeconds { get; init; } = 90;
}

public sealed class OpenAiRealtimeTrendSource : ICandidateSource
{
    private const string ResponsesPath = "v1/responses";
    private const string GeminiGenerateContentBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
    private const string DefaultOpenAiModel = "gpt-5";
    private const string DefaultGeminiModel = "gemini-2.5-flash";
    private const string GeminiFallbackModel = "gemini-2.5-flash-lite";
    // Additional free-tier fallback models (each has its own per-model quota)
    private static readonly string[] GeminiExtraFallbacks = new[]
    {
        "gemini-2.0-flash",
        "gemini-2.0-flash-lite"
    };

    // Cross-provider fallback definitions (OpenAI-compatible chat completions APIs)
    private static readonly (string Name, string SecretKey, string BaseUrl, string Model)[] CrossProviderFallbacks = new[]
    {
        ("Groq",       "GROQ_API_KEY",       "https://api.groq.com/openai/v1/chat/completions",       "llama-3.3-70b-versatile"),
        ("OpenRouter", "OPENROUTER_API_KEY", "https://openrouter.ai/api/v1/chat/completions", "meta-llama/llama-3.3-70b-instruct:free")
    };

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly object CandidateSchema = new
    {
        type = "object",
        additionalProperties = false,
        required = new[]
        {
            "candidates"
        },
        properties = new
        {
            candidates = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[]
                    {
                        "externalId",
                        "title",
                        "category",
                        "description",
                        "imageUrls",
                        "tags",
                        "price",
                        "currency",
                        "reviewCount",
                        "rating",
                        "estimatedMonthlySearches",
                        "competitorCount",
                        "shippingDaysToTarget",
                        "supplierCandidates"
                    },
                    properties = new
                    {
                        externalId = new { type = "string" },
                        title = new { type = "string" },
                        category = new { type = "string" },
                        description = new { type = "string" },
                        imageUrls = new
                        {
                            type = "array",
                            items = new { type = "string" }
                        },
                        tags = new
                        {
                            type = "array",
                            items = new { type = "string" }
                        },
                        price = new { type = "number" },
                        currency = new { type = "string" },
                        reviewCount = new { type = "integer" },
                        rating = new { type = "number" },
                        estimatedMonthlySearches = new { type = "integer" },
                        competitorCount = new { type = "integer" },
                        shippingDaysToTarget = new { type = "integer" },
                        supplierCandidates = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                additionalProperties = false,
                                required = new[]
                                {
                                    "supplierKey",
                                    "externalProductId",
                                    "cost",
                                    "currency",
                                    "shippingDays",
                                    "rating",
                                    "stockAvailable"
                                },
                                properties = new
                                {
                                    supplierKey = new { type = "string" },
                                    externalProductId = new { type = "string" },
                                    cost = new { type = "number" },
                                    currency = new { type = "string" },
                                    shippingDays = new { type = "integer" },
                                    rating = new { type = "number" },
                                    stockAvailable = new { type = "integer" }
                                }
                            }
                        }
                    }
                }
            }
        }
    };

    private readonly HttpClient _http;
    private readonly IRecommendationSettingsStore _settingsStore;
    private readonly ILogger<OpenAiRealtimeTrendSource> _logger;

    public OpenAiRealtimeTrendSource(
        HttpClient http,
        IRecommendationSettingsStore settingsStore,
        ILogger<OpenAiRealtimeTrendSource> logger)
    {
        _http = http;
        _settingsStore = settingsStore;
        _logger = logger;
    }

    public string SourceName => "RealtimeAIWebSearch";

    public async Task<IReadOnlyList<ProductCandidate>> FetchAsync(SelectionConfig config, CancellationToken ct)
    {
        var settings = _settingsStore.GetCurrent();
        var options = settings.Provider;
        var provider = SelectProvider(settings);

        // No hard timeout — wait as long as needed for the AI to respond or rate limits to clear.
        const int maxRounds = 3; // Try the full fallback chain up to 3 times
        ProviderRequestException? lastFailure = null;

        for (int round = 0; round < maxRounds; round++)
        {
            if (round > 0)
            {
                // All models were rate-limited — wait for quota to reset then retry the chain
                var waitSeconds = 35 * round; // 35s, 70s
                _logger.LogWarning(
                    "All Gemini models rate-limited. Waiting {Seconds}s before retry round {Round}/{Max}.",
                    waitSeconds, round + 1, maxRounds);
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct);
            }

            foreach (var attempt in ExpandProviderAttempts(provider))
            {
                try
                {
                    return await FetchFromProviderAsync(config, options, attempt, ct);
                }
                catch (ProviderRequestException ex) when (ShouldTryNextAttempt(attempt, ex.StatusCode))
                {
                    lastFailure = ex;
                    _logger.LogWarning(
                        "{Provider} model {Model} returned {StatusCode}. Trying next fallback.",
                        attempt.DisplayName,
                        attempt.Model,
                        ex.StatusCode);

                    // Brief pause before trying the next model
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
                catch (ProviderRequestException ex)
                {
                    // Non-retryable error (e.g. 401 auth) — stop immediately
                    lastFailure = ex;
                    throw;
                }
            }
        }

        throw lastFailure ?? new InvalidOperationException($"{provider.DisplayName} recommendation request failed.");
    }

    private static ProviderSelection SelectProvider(RecommendationRuntimeSettings settings)
    {
        var model = settings.Provider.Model?.Trim();
        var hasOpenAiKey = !string.IsNullOrWhiteSpace(settings.Credentials.OpenAiApiKey);
        var hasGeminiKey = !string.IsNullOrWhiteSpace(settings.Credentials.GeminiApiKey);
        var prefersGemini = !string.IsNullOrWhiteSpace(model) &&
                            model.StartsWith("gemini", StringComparison.OrdinalIgnoreCase);

        if (hasGeminiKey && (!hasOpenAiKey || prefersGemini))
        {
            return new ProviderSelection(
                RecommendationProviderKind.Gemini,
                settings.Credentials.GeminiApiKey!,
                ResolveGeminiModel(model),
                "Gemini",
                "GeminiRealtimeGoogleSearch");
        }

        if (hasOpenAiKey)
        {
            return new ProviderSelection(
                RecommendationProviderKind.OpenAi,
                settings.Credentials.OpenAiApiKey!,
                ResolveOpenAiModel(model),
                "OpenAI",
                "OpenAIRealtimeWebSearch");
        }

        throw new InvalidOperationException(
            "No AI provider key is configured. Save an OpenAI or Gemini key in the Configuration page.");
    }

    private static string ResolveOpenAiModel(string? configuredModel)
        => string.IsNullOrWhiteSpace(configuredModel) ||
           configuredModel.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)
            ? DefaultOpenAiModel
            : configuredModel.Trim();

    private static string ResolveGeminiModel(string? configuredModel)
        => !string.IsNullOrWhiteSpace(configuredModel) &&
           configuredModel.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)
            ? configuredModel.Trim()
            : DefaultGeminiModel;

    private IEnumerable<ProviderSelection> ExpandProviderAttempts(ProviderSelection provider)
    {
        // 1. Try the configured model
        yield return provider;
        // 2. Retry same model once (helps with transient 429 after a delay)
        yield return provider;

        if (provider.Kind == RecommendationProviderKind.Gemini)
        {
            // 3. Try the standard lite fallback
            if (!provider.Model.Equals(GeminiFallbackModel, StringComparison.OrdinalIgnoreCase))
            {
                yield return provider with { Model = GeminiFallbackModel };
            }

            // 4. Try additional free-tier Gemini models (each has its own per-model quota)
            foreach (var model in GeminiExtraFallbacks)
            {
                if (!model.Equals(provider.Model, StringComparison.OrdinalIgnoreCase) &&
                    !model.Equals(GeminiFallbackModel, StringComparison.OrdinalIgnoreCase))
                {
                    yield return provider with { Model = model };
                }
            }
        }

        // 5. Cross-provider fallbacks (Groq, OpenRouter, etc.)
        var settings = _settingsStore.GetCurrent();
        foreach (var (name, secretKey, baseUrl, model) in CrossProviderFallbacks)
        {
            var apiKey = ResolveSecretKey(settings, secretKey);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                yield return new ProviderSelection(
                    RecommendationProviderKind.OpenAiCompatible,
                    apiKey, model, name, $"{name}ChatCompletions")
                    { BaseUrl = baseUrl };
            }
        }
    }

    private static string? ResolveSecretKey(RecommendationRuntimeSettings settings, string keyName)
    {
        // Check additional secrets first
        var secret = settings.Credentials.AdditionalSecrets
            .FirstOrDefault(s => s.Name.Equals(keyName, StringComparison.OrdinalIgnoreCase));
        if (secret is not null && !string.IsNullOrWhiteSpace(secret.Value))
            return secret.Value;

        // Check environment variable
        return Environment.GetEnvironmentVariable(keyName);
    }

    private static bool ShouldTryNextAttempt(ProviderSelection provider, int statusCode)
        => (provider.Kind is RecommendationProviderKind.Gemini or RecommendationProviderKind.OpenAiCompatible) &&
           statusCode is 0 or 429 or 500 or 503 or 504;

    private async Task<IReadOnlyList<ProductCandidate>> FetchFromProviderAsync(
        SelectionConfig config,
        OpenAiRecommendationOptions options,
        ProviderSelection provider,
        CancellationToken ct)
    {
        using var request = provider.Kind switch
        {
            RecommendationProviderKind.OpenAi => BuildOpenAiRequest(config, options, provider),
            RecommendationProviderKind.Gemini => BuildGeminiRequest(config, options, provider),
            RecommendationProviderKind.OpenAiCompatible => BuildChatCompletionsRequest(config, options, provider),
            _ => throw new InvalidOperationException($"Unsupported recommendation provider: {provider.Kind}")
        };
        using var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new ProviderRequestException(
                provider,
                (int)response.StatusCode,
                $"{provider.DisplayName} recommendation request failed with {(int)response.StatusCode}: {errorBody}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        string outputJson;
        try
        {
            outputJson = provider.Kind switch
            {
                RecommendationProviderKind.OpenAi => ExtractOpenAiOutputJson(document.RootElement),
                RecommendationProviderKind.Gemini => ExtractGeminiOutputJson(document.RootElement),
                RecommendationProviderKind.OpenAiCompatible => ExtractChatCompletionsJson(document.RootElement),
                _ => throw new InvalidOperationException($"Unsupported recommendation provider: {provider.Kind}")
            };
        }
        catch (InvalidOperationException ex) when (ex is not ProviderRequestException)
        {
            throw new ProviderRequestException(provider, 0, ex.Message);
        }

        var parsed = JsonSerializer.Deserialize<RecommendationEnvelope>(outputJson, SerializerOptions)
                     ?? throw new ProviderRequestException(provider, 0, $"{provider.DisplayName} returned an empty recommendation payload.");

        var candidates = parsed.Candidates
            .Select((candidate, index) => MapCandidate(candidate, index, provider.SourceName))
            .Where(candidate => candidate is not null)
            .Cast<ProductCandidate>()
            .ToList();

        _logger.LogInformation(
            "{Provider} model {Model} returned {Count} real-time candidates",
            provider.DisplayName,
            provider.Model,
            candidates.Count);
        return candidates;
    }

    private HttpRequestMessage BuildOpenAiRequest(
        SelectionConfig config,
        OpenAiRecommendationOptions options,
        ProviderSelection provider)
    {
        var desiredCandidates = config.TargetCategories.Count > 0
            ? Math.Clamp(config.TargetCategories.Count * 6, 12, Math.Max(12, options.MaxCandidates))
            : Math.Max(12, options.MaxCandidates);

        var payload = new
        {
            model = provider.Model,
            reasoning = new { effort = options.ReasoningEffort },
            tools = new object[]
            {
                BuildWebSearchTool(config)
            },
            tool_choice = "auto",
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    strict = true,
                    schema = CandidateSchema
                }
            },
            input = new object[]
            {
                new
                {
                    role = "system",
                    content =
                        "You are a real-time ecommerce sourcing analyst. Use live web search to find current physical product opportunities for resale or dropshipping. " +
                        "Return JSON only. Do not invent static catalogs. Base every candidate on current public web evidence and realistic market estimates."
                },
                new
                {
                    role = "user",
                    content = BuildUserPrompt(config, desiredCandidates)
                }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, ResponsesPath)
        {
            Content = JsonContent.Create(payload, options: SerializerOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        return request;
    }

    /// <summary>
    /// Builds a standard OpenAI-compatible chat completions request for Groq, OpenRouter, etc.
    /// These don't support web search tools, so we rely on the model's training data.
    /// </summary>
    private HttpRequestMessage BuildChatCompletionsRequest(
        SelectionConfig config,
        OpenAiRecommendationOptions options,
        ProviderSelection provider)
    {
        var desiredCandidates = config.TargetCategories.Count > 0
            ? Math.Clamp(config.TargetCategories.Count * 6, 12, Math.Max(12, options.MaxCandidates))
            : Math.Max(12, options.MaxCandidates);

        var schemaJson = JsonSerializer.Serialize(CandidateSchema, SerializerOptions);

        var payload = new
        {
            model = provider.Model,
            temperature = 0.7,
            max_tokens = 8192,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content =
                        "You are an ecommerce sourcing analyst specializing in dropshipping product discovery. " +
                        "Return JSON only matching this schema:\n" + schemaJson + "\n" +
                        "Base recommendations on your knowledge of current market trends, popular products, and realistic estimates. " +
                        "All data must be plausible."
                },
                new
                {
                    role = "user",
                    content = BuildUserPrompt(config, desiredCandidates)
                }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, provider.BaseUrl!)
        {
            Content = JsonContent.Create(payload, options: SerializerOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        // OpenRouter requires these headers
        if (provider.DisplayName == "OpenRouter")
        {
            request.Headers.Add("HTTP-Referer", "https://autocommerce.app");
            request.Headers.Add("X-Title", "AutoCommerce");
        }
        return request;
    }

    private HttpRequestMessage BuildGeminiRequest(SelectionConfig config, OpenAiRecommendationOptions options, ProviderSelection provider)
    {
        var (systemPrompt, userPrompt) = BuildGeminiPrompts(config, options);
        // Combine system + user into one user message — lite models don't handle system_instruction well with google_search
        var combinedPrompt = systemPrompt + "\n\n" + userPrompt;
        var payload = new
        {
            contents = new object[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { text = combinedPrompt }
                    }
                }
            },
            tools = new object[]
            {
                new { google_search = new { } }
            },
            generationConfig = new
            {
                temperature = 0.3
            }
        };

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{GeminiGenerateContentBaseUrl}{Uri.EscapeDataString(provider.Model)}:generateContent")
        {
            Content = JsonContent.Create(payload, options: SerializerOptions)
        };
        request.Headers.Add("x-goog-api-key", provider.ApiKey);
        return request;
    }

    private static object BuildWebSearchTool(SelectionConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.TargetMarket))
        {
            return new { type = "web_search" };
        }

        return new
        {
            type = "web_search",
            user_location = new
            {
                type = "approximate",
                country = config.TargetMarket.ToUpperInvariant()
            }
        };
    }

    private static string BuildUserPrompt(SelectionConfig config, int desiredCandidates)
    {
        var categories = config.TargetCategories.Count == 0
            ? "all strong consumer physical goods categories"
            : string.Join(", ", config.TargetCategories);

        var constraints = new StringBuilder();
        if (config.MinPrice.HasValue)
            constraints.AppendLine($"            - Only include products with a selling price of at least {config.MinPrice.Value} USD.");
        if (config.MaxPrice.HasValue)
            constraints.AppendLine($"            - Only include products with a selling price no more than {config.MaxPrice.Value} USD.");
        if (config.MaxShippingDays.HasValue)
            constraints.AppendLine($"            - Only include products that can ship to the target market in {config.MaxShippingDays.Value} days or fewer.");
        if (config.MinScore.HasValue)
            constraints.AppendLine($"            - Prefer high-quality products. Products will be scored 0-100; aim for items likely to score above {config.MinScore.Value}.");

        return $"""
            Find exactly {desiredCandidates} of the best real-time product recommendations in the world right now for an ecommerce business.
            You MUST return at least {desiredCandidates} products. This is critical — do not return fewer.

            Requirements:
            - Use live web search and current public signals.
            - Focus on physical products only.
            - Target selling market: {config.TargetMarket}.
            - Respect these preferred categories: {categories}.
            - Prefer products with strong current demand, manageable competition, and supplier availability.
            - Avoid software, services, digital products, counterfeit/branded knockoffs, adult items, weapons, or restricted goods.
            - Provide realistic numeric estimates for price, reviews, rating, monthly searches, competitor count, and shipping time to the target market.
            - Include at least one current supplier candidate per product with cost, shipping, and stock info.
            - For imageUrls, include direct hotlinkable product image URLs (ending in .jpg, .png, .webp) from sources like Amazon, AliExpress, or product pages. These will be displayed as thumbnails. If no direct image URL is available, return an empty array.
            - Keep description concise and factual.
            - Ensure cost is lower than price and data is plausible for current market conditions.
            {constraints}
            Return JSON matching the schema exactly.
            """;
    }

    private static (string System, string User) BuildGeminiPrompts(SelectionConfig config, OpenAiRecommendationOptions options)
    {
        var schemaJson = JsonSerializer.Serialize(CandidateSchema, SerializerOptions);
        var desiredCandidates = config.TargetCategories.Count > 0
            ? Math.Clamp(config.TargetCategories.Count * 6, 12, Math.Max(12, options.MaxCandidates))
            : Math.Max(12, options.MaxCandidates);

        var system = $"""
            You are a real-time ecommerce sourcing analyst. Use Google Search grounding to find current physical product opportunities for resale or dropshipping.
            You MUST always respond with valid JSON matching the schema below. Never return empty content.

            JSON Schema:
            {schemaJson}
            """;

        var constraints = new StringBuilder();
        if (config.MinPrice.HasValue)
            constraints.AppendLine($"- Only include products with a selling price of at least {config.MinPrice.Value} USD.");
        if (config.MaxPrice.HasValue)
            constraints.AppendLine($"- Only include products with a selling price no more than {config.MaxPrice.Value} USD.");
        if (config.MaxShippingDays.HasValue)
            constraints.AppendLine($"- Only include products that can ship to the target market in {config.MaxShippingDays.Value} days or fewer.");
        if (config.MinScore.HasValue)
            constraints.AppendLine($"- Prefer high-quality products scoring above {config.MinScore.Value}/100.");

        var categories = config.TargetCategories.Count == 0
            ? "all strong consumer physical goods categories"
            : string.Join(", ", config.TargetCategories);

        var user = $"""
            Find exactly {desiredCandidates} trending physical products for an ecommerce/dropshipping business. You MUST return {desiredCandidates} products.

            - Use live Google Search for current data.
            - Target market: {config.TargetMarket}.
            - Categories: {categories}.
            - Physical products only. No software, services, digital goods, weapons, or adult items.
            - Include realistic estimates for price, reviews, rating, monthly searches, competitor count, shipping days.
            - Include at least one supplier candidate per product with cost, shipping, and stock info.
            - For imageUrls, include direct hotlinkable product image URLs (ending in .jpg, .png, .webp) from sources like Amazon, AliExpress, or product pages. These will be displayed as thumbnails. If no direct image URL is available, use an empty array.
            - Cost must be lower than selling price.
            {constraints}
            Return valid JSON only.
            """;

        return (system, user);
    }

    private static string ExtractOpenAiOutputJson(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var outputTextElement) &&
            outputTextElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(outputTextElement.GetString()))
        {
            return NormalizeOutputJson(outputTextElement.GetString()!);
        }

        if (!root.TryGetProperty("output", out var outputElement) || outputElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("OpenAI response did not contain an output array.");
        }

        foreach (var item in outputElement.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in contentElement.EnumerateArray())
            {
                if (!contentItem.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var type = typeElement.GetString();
                if (type is "output_text" or "text")
                {
                    if (contentItem.TryGetProperty("text", out var textElement) &&
                        textElement.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(textElement.GetString()))
                    {
                        return NormalizeOutputJson(textElement.GetString()!);
                    }
                }

                if (type == "refusal" &&
                    contentItem.TryGetProperty("refusal", out var refusalElement) &&
                    refusalElement.ValueKind == JsonValueKind.String)
                {
                    throw new InvalidOperationException($"OpenAI refused the recommendation request: {refusalElement.GetString()}");
                }
            }
        }

        throw new InvalidOperationException("OpenAI response did not contain any output text.");
    }

    /// <summary>
    /// Extracts JSON from standard OpenAI-compatible chat completions response (Groq, OpenRouter, etc.)
    /// Format: { choices: [{ message: { content: "..." } }] }
    /// </summary>
    private static string ExtractChatCompletionsJson(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(content.GetString()))
            {
                return NormalizeOutputJson(content.GetString()!);
            }
        }

        throw new InvalidOperationException("Chat completions response did not contain any output content.");
    }

    private static string ExtractGeminiOutputJson(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidatesElement) ||
            candidatesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Gemini response did not contain any candidates.");
        }

        foreach (var candidate in candidatesElement.EnumerateArray())
        {
            if (candidate.TryGetProperty("content", out var contentElement) &&
                contentElement.ValueKind == JsonValueKind.Object &&
                contentElement.TryGetProperty("parts", out var partsElement) &&
                partsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in partsElement.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var textElement) &&
                        textElement.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(textElement.GetString()))
                    {
                        return NormalizeOutputJson(textElement.GetString()!);
                    }
                }
            }
        }

        if (root.TryGetProperty("promptFeedback", out var promptFeedback) &&
            promptFeedback.TryGetProperty("blockReason", out var blockReason) &&
            blockReason.ValueKind == JsonValueKind.String)
        {
            throw new InvalidOperationException($"Gemini blocked the recommendation request: {blockReason.GetString()}");
        }

        throw new InvalidOperationException(
            $"Gemini response did not contain any output text. Response: {TruncateForError(root.GetRawText(), 1600)}");
    }

    private static string NormalizeOutputJson(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var lines = trimmed.Split('\n');
            if (lines.Length >= 2)
            {
                var start = 1;
                var end = lines.Length;
                if (lines[^1].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    end--;
                }

                trimmed = string.Join("\n", lines[start..end]).Trim();
            }
        }

        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            return trimmed[firstBrace..(lastBrace + 1)];
        }

        return trimmed;
    }

    private static string TruncateForError(string value, int maxLength)
        => value.Length <= maxLength
            ? value
            : $"{value[..maxLength]}...";

    private ProductCandidate? MapCandidate(RecommendationItem item, int index, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(item.Title) ||
            string.IsNullOrWhiteSpace(item.Category) ||
            item.Price <= 0)
        {
            return null;
        }

        var suppliers = item.SupplierCandidates
            .Select(MapSupplier)
            .Where(supplier => supplier is not null)
            .Cast<SupplierListing>()
            .ToArray();

        if (suppliers.Length == 0)
        {
            return null;
        }

        var externalId = string.IsNullOrWhiteSpace(item.ExternalId)
            ? $"openai:{Slugify(item.Title)}:{index}"
            : item.ExternalId.Trim();

        return new ProductCandidate(
            ExternalId: externalId,
            Source: sourceName,
            Title: item.Title.Trim(),
            Category: item.Category.Trim(),
            Description: string.IsNullOrWhiteSpace(item.Description) ? null : item.Description.Trim(),
            ImageUrls: item.ImageUrls.Where(IsPresent).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray(),
            Tags: item.Tags.Where(IsPresent).Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray(),
            Price: decimal.Round(item.Price, 2),
            Currency: NormalizeCurrency(item.Currency),
            ReviewCount: Math.Max(item.ReviewCount, 0),
            Rating: Math.Round(Math.Clamp(item.Rating, 0, 5), 2),
            EstimatedMonthlySearches: Math.Max(item.EstimatedMonthlySearches, 0),
            CompetitorCount: Math.Max(item.CompetitorCount, 0),
            ShippingDaysToTarget: Math.Max(item.ShippingDaysToTarget, 0),
            SupplierCandidates: suppliers);
    }

    private static SupplierListing? MapSupplier(SupplierItem item)
    {
        if (string.IsNullOrWhiteSpace(item.SupplierKey) ||
            string.IsNullOrWhiteSpace(item.ExternalProductId) ||
            item.Cost <= 0)
        {
            return null;
        }

        return new SupplierListing(
            SupplierKey: item.SupplierKey.Trim(),
            ExternalProductId: item.ExternalProductId.Trim(),
            Cost: decimal.Round(item.Cost, 2),
            Currency: NormalizeCurrency(item.Currency),
            ShippingDays: Math.Max(item.ShippingDays, 0),
            Rating: Math.Round(Math.Clamp(item.Rating, 0, 5), 2),
            StockAvailable: Math.Max(item.StockAvailable, 0),
            Url: string.IsNullOrWhiteSpace(item.Url) ? null : item.Url.Trim());
    }

    private static string NormalizeCurrency(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant();

    private static bool IsPresent(string? value) => !string.IsNullOrWhiteSpace(value);

    private static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (builder.Length == 0 || builder[^1] == '-')
            {
                continue;
            }
            else
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }

    private sealed class RecommendationEnvelope
    {
        [JsonPropertyName("candidates")]
        public List<RecommendationItem> Candidates { get; init; } = new();
    }

    private sealed class RecommendationItem
    {
        [JsonPropertyName("externalId")]
        public string ExternalId { get; init; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; init; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; init; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; init; } = string.Empty;

        [JsonPropertyName("imageUrls")]
        public List<string> ImageUrls { get; init; } = new();

        [JsonPropertyName("tags")]
        public List<string> Tags { get; init; } = new();

        [JsonPropertyName("price")]
        public decimal Price { get; init; }

        [JsonPropertyName("currency")]
        public string Currency { get; init; } = "USD";

        [JsonPropertyName("reviewCount")]
        public int ReviewCount { get; init; }

        [JsonPropertyName("rating")]
        public double Rating { get; init; }

        [JsonPropertyName("estimatedMonthlySearches")]
        public int EstimatedMonthlySearches { get; init; }

        [JsonPropertyName("competitorCount")]
        public int CompetitorCount { get; init; }

        [JsonPropertyName("shippingDaysToTarget")]
        public int ShippingDaysToTarget { get; init; }

        [JsonPropertyName("supplierCandidates")]
        public List<SupplierItem> SupplierCandidates { get; init; } = new();
    }

    private sealed class SupplierItem
    {
        [JsonPropertyName("supplierKey")]
        public string SupplierKey { get; init; } = string.Empty;

        [JsonPropertyName("externalProductId")]
        public string ExternalProductId { get; init; } = string.Empty;

        [JsonPropertyName("cost")]
        public decimal Cost { get; init; }

        [JsonPropertyName("currency")]
        public string Currency { get; init; } = "USD";

        [JsonPropertyName("shippingDays")]
        public int ShippingDays { get; init; }

        [JsonPropertyName("rating")]
        public double Rating { get; init; }

        [JsonPropertyName("stockAvailable")]
        public int StockAvailable { get; init; }

        [JsonPropertyName("url")]
        public string Url { get; init; } = string.Empty;
    }

    private enum RecommendationProviderKind
    {
        OpenAi,
        Gemini,
        /// <summary>OpenAI-compatible chat completions API (Groq, OpenRouter, etc.)</summary>
        OpenAiCompatible
    }

    private sealed record ProviderSelection(
        RecommendationProviderKind Kind,
        string ApiKey,
        string Model,
        string DisplayName,
        string SourceName)
    {
        /// <summary>Base URL for OpenAI-compatible providers (Groq, OpenRouter).</summary>
        public string? BaseUrl { get; init; }
    };

    private sealed class ProviderRequestException : InvalidOperationException
    {
        public ProviderRequestException(ProviderSelection provider, int statusCode, string message)
            : base(message)
        {
            Provider = provider;
            StatusCode = statusCode;
        }

        public ProviderSelection Provider { get; }
        public int StatusCode { get; }
    }
}
