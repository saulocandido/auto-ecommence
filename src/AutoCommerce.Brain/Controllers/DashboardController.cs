using AutoCommerce.Brain.Services;
using AutoCommerce.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoCommerce.Brain.Controllers;

[ApiController]
[Route("api/dashboard")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly IMetricsService _metrics;
    public DashboardController(IMetricsService metrics) => _metrics = metrics;

    [HttpGet("metrics")]
    public Task<DashboardMetrics> Metrics(CancellationToken ct) => _metrics.GetDashboardAsync(ct);
}
