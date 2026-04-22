using AutoCommerce.ProductSelection.Services;
using Microsoft.AspNetCore.Mvc;

namespace AutoCommerce.ProductSelection.Controllers;

[ApiController]
[Route("configuration/recommendations")]
public class RecommendationConfigurationController : ControllerBase
{
    private readonly IRecommendationSettingsStore _settingsStore;

    public RecommendationConfigurationController(IRecommendationSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    [HttpGet]
    public ActionResult<RecommendationSettingsResponse> Get()
    {
        return Ok(_settingsStore.GetResponse());
    }

    [HttpPut]
    public async Task<ActionResult<RecommendationSettingsResponse>> Update(
        [FromBody] RecommendationSettingsUpdate update,
        CancellationToken ct)
    {
        var errors = Validate(update);
        if (errors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(errors));
        }

        var saved = await _settingsStore.UpdateAsync(update, ct);
        return Ok(saved);
    }

    private static Dictionary<string, string[]> Validate(RecommendationSettingsUpdate update)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        AddErrorIf(errors, string.IsNullOrWhiteSpace(update.Provider.Model),
            nameof(update.Provider.Model), "Model is required.");
        AddErrorIf(errors, string.IsNullOrWhiteSpace(update.Provider.ReasoningEffort),
            nameof(update.Provider.ReasoningEffort), "Reasoning effort is required.");
        AddErrorIf(errors, update.Provider.ClearApiKey && !string.IsNullOrWhiteSpace(update.Provider.ApiKey),
            nameof(update.Provider.ApiKey), "Provide a new API key or clear the existing one, not both.");
        AddErrorIf(errors, update.Credentials.ClearOpenAiApiKey && !string.IsNullOrWhiteSpace(update.Credentials.OpenAiApiKey),
            nameof(update.Credentials.OpenAiApiKey), "Provide a new OpenAI API key or clear the saved one, not both.");
        AddErrorIf(errors, update.Credentials.ClearGeminiApiKey && !string.IsNullOrWhiteSpace(update.Credentials.GeminiApiKey),
            nameof(update.Credentials.GeminiApiKey), "Provide a new Gemini API key or clear the saved one, not both.");
        AddErrorIf(errors, update.Provider.MaxCandidates < 1,
            nameof(update.Provider.MaxCandidates), "Max candidates must be at least 1.");
        AddErrorIf(errors, update.Provider.RequestTimeoutSeconds < 5,
            nameof(update.Provider.RequestTimeoutSeconds), "Request timeout must be at least 5 seconds.");
        AddErrorIf(errors, update.Selection.MinPrice.HasValue && update.Selection.MinPrice < 0,
            nameof(update.Selection.MinPrice), "Minimum price cannot be negative.");
        AddErrorIf(errors, update.Selection.MaxPrice.HasValue && update.Selection.MaxPrice <= 0,
            nameof(update.Selection.MaxPrice), "Maximum price must be greater than 0.");
        AddErrorIf(errors, update.Selection.MinPrice.HasValue && update.Selection.MaxPrice.HasValue && update.Selection.MinPrice > update.Selection.MaxPrice,
            nameof(update.Selection.MaxPrice), "Maximum price must be greater than or equal to minimum price.");
        AddErrorIf(errors, update.Selection.MinScore.HasValue && (update.Selection.MinScore < 0 || update.Selection.MinScore > 100),
            nameof(update.Selection.MinScore), "Minimum score must be between 0 and 100.");
        AddErrorIf(errors, update.Selection.TopNPerCategory < 1,
            nameof(update.Selection.TopNPerCategory), "Top N per category must be at least 1.");
        AddErrorIf(errors, string.IsNullOrWhiteSpace(update.Selection.TargetMarket),
            nameof(update.Selection.TargetMarket), "Target market is required.");
        AddErrorIf(errors, update.Selection.MaxShippingDays.HasValue && update.Selection.MaxShippingDays < 1,
            nameof(update.Selection.MaxShippingDays), "Max shipping days must be at least 1.");

        var duplicates = (update.Credentials.AdditionalSecrets ?? Array.Empty<RecommendationNamedCredentialUpdate>())
            .Where(secret => !string.IsNullOrWhiteSpace(secret.Name))
            .GroupBy(secret => secret.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        AddErrorIf(errors, duplicates.Length > 0,
            nameof(update.Credentials.AdditionalSecrets), $"Duplicate additional secret names are not allowed: {string.Join(", ", duplicates)}.");

        AddErrorIf(errors,
            (update.Credentials.AdditionalSecrets ?? Array.Empty<RecommendationNamedCredentialUpdate>())
            .Any(secret => string.IsNullOrWhiteSpace(secret.Name) && (!string.IsNullOrWhiteSpace(secret.Value) || secret.Clear)),
            nameof(update.Credentials.AdditionalSecrets), "Each additional secret must have a name.");

        return errors;
    }

    private static void AddErrorIf(
        Dictionary<string, string[]> errors,
        bool condition,
        string key,
        string message)
    {
        if (condition)
        {
            errors[key] = new[] { message };
        }
    }
}
