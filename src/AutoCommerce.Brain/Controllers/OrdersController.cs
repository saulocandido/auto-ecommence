using AutoCommerce.Brain.Services;
using AutoCommerce.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoCommerce.Brain.Controllers;

[ApiController]
[Route("api/orders")]
[Authorize]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _svc;
    public OrdersController(IOrderService svc) => _svc = svc;

    [HttpGet]
    public async Task<IReadOnlyList<OrderResponse>> List(
        [FromQuery] string? status, [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
        => await _svc.ListAsync(status, skip, take, ct);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrderResponse>> Get(Guid id, CancellationToken ct)
        => (await _svc.GetAsync(id, ct)) is { } o ? o : NotFound();

    [HttpGet("by-shop/{shopOrderId}")]
    public async Task<ActionResult<OrderResponse>> GetByShop(string shopOrderId, CancellationToken ct)
        => (await _svc.GetByShopOrderIdAsync(shopOrderId, ct)) is { } o ? o : NotFound();

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<OrderResponse>> Create([FromBody] OrderCreateDto dto, CancellationToken ct)
    {
        var o = await _svc.CreateAsync(dto, ct);
        return CreatedAtAction(nameof(Get), new { id = o.Id }, o);
    }

    [HttpPatch("{id:guid}/tracking")]
    public async Task<ActionResult<OrderResponse>> UpdateTracking(Guid id, [FromBody] OrderTrackingUpdateDto dto, CancellationToken ct)
        => (await _svc.UpdateTrackingAsync(id, dto, ct)) is { } o ? o : NotFound();
}
