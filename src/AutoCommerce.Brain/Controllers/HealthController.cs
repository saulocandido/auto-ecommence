using Microsoft.AspNetCore.Mvc;

namespace AutoCommerce.Brain.Controllers;

[ApiController]
[Route("api/health")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        status = "ok",
        service = "autocommerce-brain",
        time = DateTimeOffset.UtcNow,
        version = typeof(HealthController).Assembly.GetName().Version?.ToString() ?? "0.0.0"
    });
}
