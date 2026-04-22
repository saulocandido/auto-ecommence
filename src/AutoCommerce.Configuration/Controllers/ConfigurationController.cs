using AutoCommerce.Configuration.Services;
using Microsoft.AspNetCore.Mvc;

namespace AutoCommerce.Configuration.Controllers;

[ApiController]
[Route("configuration/recommendations")]
public class ConfigurationController : ControllerBase
{
    private readonly IConfigurationSettingsStore _store;
    private readonly IConfigurationNotifier _notifier;

    public ConfigurationController(IConfigurationSettingsStore store, IConfigurationNotifier notifier)
    {
        _store = store;
        _notifier = notifier;
    }

    /// <summary>GET — returns masked settings (for dashboard).</summary>
    [HttpGet]
    public ActionResult<ConfigurationSettingsResponse> Get() => Ok(_store.GetResponse());

    /// <summary>PUT — update settings, then notify all subscriber microservices.</summary>
    [HttpPut]
    public async Task<ActionResult<ConfigurationSettingsResponse>> Update(
        [FromBody] ConfigurationSettingsUpdate update, CancellationToken ct)
    {
        var errors = Validate(update);
        if (errors.Count > 0) return BadRequest(new ValidationProblemDetails(errors));

        var saved = await _store.UpdateAsync(update, ct);

        // Fire-and-forget notification to subscribers so they pull the latest settings
        _ = Task.Run(() => _notifier.NotifyAsync(CancellationToken.None));

        return Ok(saved);
    }

    /// <summary>GET /configuration/recommendations/internal — returns full secrets (MS-to-MS only).</summary>
    [HttpGet("internal")]
    public ActionResult<InternalSettingsResponse> GetInternal() => Ok(_store.GetInternal());

    // ── Validation (same rules as before) ──

    private static Dictionary<string, string[]> Validate(ConfigurationSettingsUpdate update)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        AddIf(errors, string.IsNullOrWhiteSpace(update.Provider.Model),
            nameof(update.Provider.Model), "Model is required.");
        AddIf(errors, string.IsNullOrWhiteSpace(update.Provider.ReasoningEffort),
            nameof(update.Provider.ReasoningEffort), "Reasoning effort is required.");
        AddIf(errors, update.Provider.ClearApiKey && !string.IsNullOrWhiteSpace(update.Provider.ApiKey),
            nameof(update.Provider.ApiKey), "Provide a new API key or clear the existing one, not both.");
        AddIf(errors, update.Credentials.ClearOpenAiApiKey && !string.IsNullOrWhiteSpace(update.Credentials.OpenAiApiKey),
            nameof(update.Credentials.OpenAiApiKey), "Provide a new OpenAI API key or clear the saved one, not both.");
        AddIf(errors, update.Credentials.ClearGeminiApiKey && !string.IsNullOrWhiteSpace(update.Credentials.GeminiApiKey),
            nameof(update.Credentials.GeminiApiKey), "Provide a new Gemini API key or clear the saved one, not both.");
        AddIf(errors, update.Provider.MaxCandidates < 1,
            nameof(update.Provider.MaxCandidates), "Max candidates must be at least 1.");
        AddIf(errors, update.Provider.RequestTimeoutSeconds < 5,
            nameof(update.Provider.RequestTimeoutSeconds), "Request timeout must be at least 5 seconds.");
        AddIf(errors, update.Selection.MinPrice.HasValue && update.Selection.MinPrice < 0,
            nameof(update.Selection.MinPrice), "Minimum price cannot be negative.");
        AddIf(errors, update.Selection.MaxPrice.HasValue && update.Selection.MaxPrice <= 0,
            nameof(update.Selection.MaxPrice), "Maximum price must be greater than 0.");
        AddIf(errors, update.Selection.MinPrice.HasValue && update.Selection.MaxPrice.HasValue && update.Selection.MinPrice > update.Selection.MaxPrice,
            nameof(update.Selection.MaxPrice), "Maximum price must be greater than or equal to minimum price.");
        AddIf(errors, update.Selection.MinScore.HasValue && (update.Selection.MinScore < 0 || update.Selection.MinScore > 100),
            nameof(update.Selection.MinScore), "Minimum score must be between 0 and 100.");
        AddIf(errors, update.Selection.TopNPerCategory < 1,
            nameof(update.Selection.TopNPerCategory), "Top N per category must be at least 1.");
        AddIf(errors, string.IsNullOrWhiteSpace(update.Selection.TargetMarket),
            nameof(update.Selection.TargetMarket), "Target market is required.");
        AddIf(errors, update.Selection.MaxShippingDays.HasValue && update.Selection.MaxShippingDays < 1,
            nameof(update.Selection.MaxShippingDays), "Max shipping days must be at least 1.");

        var dupes = (update.Credentials.AdditionalSecrets ?? Array.Empty<NamedCredentialUpdateDto>())
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(s => s.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        AddIf(errors, dupes.Length > 0,
            nameof(update.Credentials.AdditionalSecrets), $"Duplicate additional secret names: {string.Join(", ", dupes)}.");
        AddIf(errors,
            (update.Credentials.AdditionalSecrets ?? Array.Empty<NamedCredentialUpdateDto>())
            .Any(s => string.IsNullOrWhiteSpace(s.Name) && (!string.IsNullOrWhiteSpace(s.Value) || s.Clear)),
            nameof(update.Credentials.AdditionalSecrets), "Each additional secret must have a name.");

        return errors;
    }

    private static void AddIf(Dictionary<string, string[]> e, bool cond, string key, string msg)
    {
        if (cond) e[key] = new[] { msg };
    }
}

[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "healthy", service = "configuration" });
}
