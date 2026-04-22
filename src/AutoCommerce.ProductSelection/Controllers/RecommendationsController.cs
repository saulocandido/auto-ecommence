using AutoCommerce.ProductSelection.Services;
using AutoCommerce.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace AutoCommerce.ProductSelection.Controllers;

[ApiController]
[Route("recommendations")]
public class RecommendationsController : ControllerBase
{
    private readonly ISelectionOrchestrator _orch;

    public RecommendationsController(ISelectionOrchestrator orch) => _orch = orch;

    [HttpGet]
    public Task<RecommendationResponse> Get(CancellationToken ct) => _orch.GenerateAsync(null, ct);

    [HttpPost("preview")]
    public Task<RecommendationResponse> Preview([FromBody] SelectionConfig config, CancellationToken ct)
        => _orch.GenerateAsync(config, ct);
}

[ApiController]
[Route("scan")]
public class ScanController : ControllerBase
{
    private readonly ISelectionOrchestrator _orch;
    public ScanController(ISelectionOrchestrator orch) => _orch = orch;

    [HttpPost]
    public async Task<IActionResult> Run([FromBody] SelectionConfig? config, CancellationToken ct)
    {
        var (imported, total, approved) = await _orch.DiscoverAndImportAsync(config, ct);
        return Ok(new { imported, total, approved, at = DateTimeOffset.UtcNow });
    }
}

[ApiController]
[Route("links")]
public class LinksController : ControllerBase
{
    private readonly ILinkValidator _validator;
    public LinksController(ILinkValidator validator) => _validator = validator;

    [HttpPost("validate")]
    public async Task<IActionResult> Validate(CancellationToken ct)
    {
        var report = await _validator.ValidateRecommendationsAsync(ct);
        return Ok(report);
    }
}

[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok", service = "product-selection", time = DateTimeOffset.UtcNow });
}
