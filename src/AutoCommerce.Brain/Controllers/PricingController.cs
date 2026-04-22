using AutoCommerce.Brain.Services;
using AutoCommerce.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoCommerce.Brain.Controllers;

[ApiController]
[Route("api/pricing")]
[Authorize]
public class PricingController : ControllerBase
{
    private readonly IPricingEngine _engine;
    public PricingController(IPricingEngine engine) => _engine = engine;

    [HttpGet("rules")]
    public Task<IReadOnlyList<PricingRuleDto>> ListRules(CancellationToken ct) => _engine.ListRulesAsync(ct);

    [HttpPut("rules")]
    public Task<PricingRuleDto> UpsertRule([FromBody] PricingRuleDto dto, CancellationToken ct) => _engine.UpsertRuleAsync(dto, ct);

    [HttpPost("price/{productId:guid}")]
    public async Task<ActionResult<PriceUpdateDto>> SetPrice(Guid productId, [FromBody] SetPriceRequest body, CancellationToken ct)
        => (await _engine.SetPriceAsync(productId, body.Price, ct)) is { } r ? r : NotFound();

    public record SetPriceRequest(decimal Price);
}
