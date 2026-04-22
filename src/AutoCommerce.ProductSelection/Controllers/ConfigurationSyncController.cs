using AutoCommerce.ProductSelection.Services;
using Microsoft.AspNetCore.Mvc;

namespace AutoCommerce.ProductSelection.Controllers;

/// <summary>
/// Webhook endpoint called by the Configuration MS when settings change.
/// </summary>
[ApiController]
[Route("configuration")]
public class ConfigurationSyncController : ControllerBase
{
    private readonly IRecommendationSettingsStore _settingsStore;
    private readonly ILogger<ConfigurationSyncController> _logger;

    public ConfigurationSyncController(
        IRecommendationSettingsStore settingsStore,
        ILogger<ConfigurationSyncController> logger)
    {
        _settingsStore = settingsStore;
        _logger = logger;
    }

    /// <summary>POST /configuration/sync — called by Configuration MS after a settings change.</summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        if (_settingsStore is RemoteConfigurationClient remote)
        {
            _logger.LogInformation("Configuration sync webhook received, refreshing settings...");
            await remote.RefreshAsync(ct);
            return Ok(new { synced = true });
        }

        _logger.LogWarning("Configuration sync called but settings store is not remote");
        return Ok(new { synced = false, reason = "local store" });
    }
}
