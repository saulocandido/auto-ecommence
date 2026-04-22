using AutoCommerce.Brain.Domain;
using AutoCommerce.Brain.Infrastructure;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AutoCommerce.Brain.Controllers;

[ApiController]
[Route("api/events")]
[Authorize]
public class EventsController : ControllerBase
{
    private readonly IEventBus _bus;
    private readonly BrainDbContext _db;

    public EventsController(IEventBus bus, BrainDbContext db)
    {
        _bus = bus;
        _db = db;
    }

    [HttpPost("publish")]
    public async Task<IActionResult> Publish([FromBody] DomainEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.Type))
            return BadRequest(new { error = "type required" });
        var toPublish = new DomainEvent
        {
            Id = evt.Id == Guid.Empty ? Guid.NewGuid() : evt.Id,
            Type = evt.Type,
            Source = string.IsNullOrWhiteSpace(evt.Source) ? "external" : evt.Source,
            OccurredAt = evt.OccurredAt == default ? DateTimeOffset.UtcNow : evt.OccurredAt,
            Payload = evt.Payload
        };
        await _bus.PublishAsync(toPublish, ct);
        return Accepted(new { id = toPublish.Id });
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? type,
        [FromQuery] int take = 50,
        [FromQuery] DateTimeOffset? since = null,
        [FromQuery] bool includePayload = false,
        CancellationToken ct = default)
    {
        var q = _db.EventLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(type)) q = q.Where(e => e.Type == type);
        if (since.HasValue) q = q.Where(e => e.OccurredAt > since.Value);

        var capped = Math.Clamp(take, 1, 500);
        var rows = await q.OrderByDescending(e => e.OccurredAt).Take(capped).ToListAsync(ct);

        if (includePayload)
        {
            var withPayload = rows.OrderBy(e => e.OccurredAt)
                .Select(e => new RecentEventWithPayload(e.Id, e.Type, e.Source, e.OccurredAt, e.PayloadJson))
                .ToList();
            return Ok(withPayload);
        }

        var list = rows
            .Select(e => new RecentEvent(e.Id, e.Type, e.Source, e.OccurredAt))
            .ToList();
        return Ok(list);
    }
}
