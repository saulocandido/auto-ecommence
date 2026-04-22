using Microsoft.AspNetCore.Mvc;

namespace AutoCommerce.SupplierSelection.Controllers;

[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok", service = "supplier-selection", timestamp = DateTimeOffset.UtcNow });
}
