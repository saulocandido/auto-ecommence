using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoCommerce.StoreManagement.Infrastructure;
using AutoCommerce.StoreManagement.Services;

namespace AutoCommerce.StoreManagement.Controllers;

[ApiController]
[Route("api/shopify/automation")]
public class ShopifyAutomationController : ControllerBase
{
    private readonly IShopifyAutomationService _svc;
    private readonly IShopifySessionManager? _sessions;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ShopifyAutomationController> _logger;

    public ShopifyAutomationController(
        IShopifyAutomationService svc,
        IServiceScopeFactory scopeFactory,
        ILogger<ShopifyAutomationController> logger,
        IShopifySessionManager? sessions = null)
    {
        _svc = svc;
        _sessions = sessions;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ── Configuration ──

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig(CancellationToken ct) =>
        Ok(await _svc.GetConfigAsync(ct));

    [HttpPut("config")]
    public async Task<IActionResult> UpdateConfig([FromBody] AutomationConfigUpdateDto body, CancellationToken ct) =>
        Ok(await _svc.UpdateConfigAsync(body, ct));

    // ── Session management ──

    [HttpGet("session")]
    public async Task<IActionResult> GetSession(CancellationToken ct)
    {
        if (_sessions == null) return Ok(new ShopifySessionStatusDto("unknown", null, null, false, "Session manager not available"));
        return Ok(await _sessions.GetStatusAsync(ct));
    }

    [HttpPost("session/validate")]
    public async Task<IActionResult> ValidateSession(CancellationToken ct)
    {
        if (_sessions == null) return StatusCode(503, new { error = "Session manager not available" });
        var cfg = await LoadConfigDomainAsync(ct);
        if (cfg == null) return BadRequest(new { error = "Automation config not initialised" });
        return Ok(await _sessions.ValidateAsync(cfg, ct));
    }

    [HttpPost("session/connect")]
    public async Task<IActionResult> ConnectSession(CancellationToken ct)
    {
        if (_sessions == null) return StatusCode(503, new { error = "Session manager not available" });
        var cfg = await LoadConfigDomainAsync(ct);
        if (cfg == null) return BadRequest(new { error = "Automation config not initialised" });
        return Ok(await _sessions.StartInteractiveLoginAsync(cfg, ct));
    }

    public record CredentialLoginDto(string Email, string Password);

    [HttpPost("session/login")]
    public async Task<IActionResult> LoginWithCredentials([FromBody] CredentialLoginDto body, CancellationToken ct)
    {
        if (_sessions == null) return StatusCode(503, new { error = "Session manager not available" });
        if (body == null || string.IsNullOrWhiteSpace(body.Email))
            return BadRequest(new { error = "Email is required" });
        var cfg = await LoadConfigDomainAsync(ct);
        if (cfg == null) return BadRequest(new { error = "Automation config not initialised" });

        // Use saved password if frontend sends placeholder
        var email = body.Email;
        var password = body.Password;
        if (password == "__saved__")
        {
            if (string.IsNullOrWhiteSpace(cfg.ShopifyPassword))
                return BadRequest(new { error = "No saved password found. Please enter your password." });
            password = cfg.ShopifyPassword;
            if (string.IsNullOrWhiteSpace(email) || email == cfg.ShopifyEmail)
                email = cfg.ShopifyEmail ?? email;
        }
        if (string.IsNullOrWhiteSpace(password))
            return BadRequest(new { error = "Password is required" });

        return Ok(await _sessions.LoginWithCredentialsAsync(email!, password, cfg, ct));
    }

    public record VerificationCodeDto(string Code);

    [HttpPost("session/login/verify")]
    public async Task<IActionResult> SubmitVerificationCode([FromBody] VerificationCodeDto body, CancellationToken ct)
    {
        if (_sessions == null) return StatusCode(503, new { error = "Session manager not available" });
        if (body == null || string.IsNullOrWhiteSpace(body.Code))
            return BadRequest(new { error = "Verification code is required" });
        var cfg = await LoadConfigDomainAsync(ct);
        if (cfg == null) return BadRequest(new { error = "Automation config not initialised" });
        return Ok(await _sessions.SubmitVerificationCodeAsync(body.Code, cfg, ct));
    }

    [HttpGet("session/debug/screenshot")]
    public IActionResult GetDebugScreenshot()
    {
        if (_sessions == null) return StatusCode(503);
        var debugDir = Path.Combine(Path.GetDirectoryName(_sessions.StorageStatePath)!, "debug");
        if (!Directory.Exists(debugDir)) return NotFound(new { error = "No debug screenshots" });
        var files = Directory.GetFiles(debugDir, "*.png").OrderByDescending(f => f).ToArray();
        if (files.Length == 0) return NotFound(new { error = "No screenshots found" });
        var latest = files[0];
        return PhysicalFile(latest, "image/png", Path.GetFileName(latest));
    }

    [HttpGet("session/debug/screenshots")]
    public IActionResult ListDebugScreenshots()
    {
        if (_sessions == null) return StatusCode(503);
        var debugDir = Path.Combine(Path.GetDirectoryName(_sessions.StorageStatePath)!, "debug");
        if (!Directory.Exists(debugDir)) return Ok(Array.Empty<string>());
        var files = Directory.GetFiles(debugDir, "*.png").OrderByDescending(f => f)
            .Select(Path.GetFileName).ToArray();
        return Ok(files);
    }

    // ── Manual browser login (noVNC) ──

    [HttpPost("session/manual-login")]
    public async Task<IActionResult> StartManualLogin(CancellationToken ct)
    {
        if (_sessions == null) return StatusCode(503, new { error = "Session manager not available" });
        var cfg = await LoadConfigDomainAsync(ct);
        if (cfg == null) return BadRequest(new { error = "Automation config not initialised" });
        return Ok(await _sessions.StartManualBrowserLoginAsync(cfg, ct));
    }

    [HttpGet("session/manual-login/poll")]
    public async Task<IActionResult> PollManualLogin(CancellationToken ct)
    {
        if (_sessions == null) return StatusCode(503, new { error = "Session manager not available" });
        var cfg = await LoadConfigDomainAsync(ct);
        if (cfg == null) return BadRequest(new { error = "Automation config not initialised" });
        return Ok(await _sessions.PollManualLoginAsync(cfg, ct));
    }

    [HttpPost("session/manual-login/stop")]
    public async Task<IActionResult> StopManualLogin(CancellationToken ct)
    {
        if (_sessions == null) return StatusCode(503, new { error = "Session manager not available" });
        await _sessions.StopManualLoginAsync();
        return Ok(new { status = "stopped" });
    }

    public record SessionImportDto(string StorageState);

    [HttpPost("session/upload")]
    public async Task<IActionResult> UploadSession([FromBody] SessionImportDto body, CancellationToken ct)
    {
        if (_sessions == null) return StatusCode(503, new { error = "Session manager not available" });
        if (body == null || string.IsNullOrWhiteSpace(body.StorageState))
            return BadRequest(new { error = "StorageState JSON is required" });

        // Step 1: save to disk.
        var imported = await _sessions.ImportStorageStateAsync(body.StorageState, ct);
        if (imported.State == "error") return Ok(imported);

        // Step 2: IMMEDIATELY validate against Shopify admin so the UI doesn't report
        // "connected" for a storage state that turns out to be authless.
        var cfg = await LoadConfigDomainAsync(ct);
        if (cfg == null || string.IsNullOrWhiteSpace(cfg.FindProductsUrl))
            return Ok(imported); // no target URL configured yet; user can run Test later
        var validated = await _sessions.ValidateAsync(cfg, ct);
        return Ok(validated);
    }

    // ── Run management ──

    [HttpPost("run")]
    public async Task<IActionResult> StartRun(CancellationToken ct)
    {
        try
        {
            var run = await _svc.StartRunAsync(ct);
            return Ok(run);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("run/{runId:guid}/stop")]
    public async Task<IActionResult> StopRun(Guid runId, CancellationToken ct)
    {
        try { return Ok(await _svc.StopRunAsync(runId, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("run/{runId:guid}/retry")]
    public async Task<IActionResult> RetryFailed(Guid runId, CancellationToken ct)
    {
        try { return Ok(await _svc.RetryFailedAsync(runId, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("run/{runId:guid}/resume")]
    public async Task<IActionResult> ResumeRun(Guid runId, CancellationToken ct)
    {
        try { return Ok(await _svc.ResumeRunAsync(runId, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("run/active")]
    public async Task<IActionResult> GetActiveRun(CancellationToken ct)
    {
        var run = await _svc.GetActiveRunAsync(ct);
        return run == null ? NotFound(new { status = "no_active_run" }) : Ok(run);
    }

    [HttpGet("runs")]
    public async Task<IActionResult> GetRuns([FromQuery] int take = 20, CancellationToken ct = default) =>
        Ok(await _svc.GetRunsAsync(take, ct));

    [HttpGet("run/{runId:guid}")]
    public async Task<IActionResult> GetRun(Guid runId, CancellationToken ct)
    {
        var run = await _svc.GetRunAsync(runId, ct);
        return run == null ? NotFound() : Ok(run);
    }

    [HttpGet("run/{runId:guid}/products")]
    public async Task<IActionResult> GetProducts(Guid runId, CancellationToken ct) =>
        Ok(await _svc.GetRunProductsAsync(runId, ct));

    [HttpGet("run/{runId:guid}/logs")]
    public async Task<IActionResult> GetLogs(Guid runId, [FromQuery] int take = 100, CancellationToken ct = default) =>
        Ok(await _svc.GetRunLogsAsync(runId, take, ct));

    [HttpGet("run/{runId:guid}/metrics")]
    public async Task<IActionResult> GetMetrics(Guid runId, CancellationToken ct) =>
        Ok(await _svc.GetMetricsAsync(runId, ct));

    // ── helpers ──

    private async Task<Domain.ShopifyAutomationConfig?> LoadConfigDomainAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        return await db.AutomationConfigs.FirstOrDefaultAsync(ct);
    }
}
