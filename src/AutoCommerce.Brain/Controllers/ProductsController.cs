using AutoCommerce.Brain.Services;
using AutoCommerce.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoCommerce.Brain.Controllers;

[ApiController]
[Route("api/products")]
[Authorize]
public class ProductsController : ControllerBase
{
    private readonly IProductService _svc;

    public ProductsController(IProductService svc) => _svc = svc;

    [HttpGet]
    public async Task<IReadOnlyList<ProductResponse>> List(
        [FromQuery] string? status, [FromQuery] string? category,
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
        => await _svc.ListAsync(status, category, skip, take, ct);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProductResponse>> Get(Guid id, CancellationToken ct)
    {
        var p = await _svc.GetAsync(id, ct);
        return p is null ? NotFound() : p;
    }

    [HttpPost("import")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<ProductResponse>> Import([FromBody] ProductImportDto dto, CancellationToken ct)
    {
        var p = await _svc.ImportAsync(dto, ct);
        return CreatedAtAction(nameof(Get), new { id = p.Id }, p);
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<ProductResponse>> Update(Guid id, [FromBody] ProductUpdateDto dto, CancellationToken ct)
    {
        var p = await _svc.UpdateAsync(id, dto, ct);
        return p is null ? NotFound() : p;
    }

    [HttpPost("{id:guid}/assign-supplier")]
    public async Task<ActionResult<ProductResponse>> AssignSupplier(
        Guid id, [FromBody] SupplierAssignmentRequest request, CancellationToken ct)
    {
        var p = await _svc.AssignSupplierAsync(id, request, ct);
        return p is null ? NotFound() : p;
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => (await _svc.DeleteAsync(id, ct)) ? NoContent() : NotFound();
}
