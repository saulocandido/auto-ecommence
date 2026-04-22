using AutoCommerce.Shared.Contracts;
using AutoCommerce.SupplierSelection.Services;
using Microsoft.AspNetCore.Mvc;

namespace AutoCommerce.SupplierSelection.Controllers;

[ApiController]
[Route("fulfill-order")]
public class FulfillmentController : ControllerBase
{
    private readonly IFulfillmentService _service;
    public FulfillmentController(IFulfillmentService service) => _service = service;

    [HttpPost]
    public async Task<ActionResult<FulfillmentResult>> Post([FromBody] FulfillmentRequest request, CancellationToken ct)
    {
        if (request.Quantity <= 0) return BadRequest(new { error = "quantity must be > 0" });
        var result = await _service.FulfillAsync(request, ct);
        return result.Success ? Ok(result) : StatusCode(StatusCodes.Status502BadGateway, result);
    }
}
