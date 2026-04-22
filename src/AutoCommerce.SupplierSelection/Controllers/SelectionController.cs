using AutoCommerce.Shared.Contracts;
using AutoCommerce.SupplierSelection.Services;
using Microsoft.AspNetCore.Mvc;

namespace AutoCommerce.SupplierSelection.Controllers;

[ApiController]
[Route("selection")]
public class SelectionController : ControllerBase
{
    private readonly ISelectionService _selection;
    private readonly IBrainClient _brain;

    public SelectionController(ISelectionService selection, IBrainClient brain)
    {
        _selection = selection;
        _brain = brain;
    }

    [HttpPost("{productId:guid}/assign")]
    public async Task<ActionResult<SupplierSelectionResult>> Assign(Guid productId, CancellationToken ct)
    {
        try
        {
            var result = await _selection.SelectAndAssignAsync(productId, ct);
            return result;
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("{productId:guid}/preview")]
    public async Task<ActionResult<SupplierSelectionResult>> Preview(Guid productId, CancellationToken ct)
    {
        var product = await _brain.GetProductAsync(productId, ct);
        if (product is null) return NotFound();
        return _selection.Preview(product);
    }
}
