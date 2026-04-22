using AutoCommerce.Brain.Infrastructure;
using AutoCommerce.Brain.Services;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;
using Microsoft.AspNetCore.Mvc;

namespace AutoCommerce.Brain.Controllers;

[ApiController]
[Route("api/webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly IOrderService _orders;
    private readonly IEventBus _bus;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(IOrderService orders, IEventBus bus, ILogger<WebhooksController> logger)
    {
        _orders = orders;
        _bus = bus;
        _logger = logger;
    }

    [HttpPost("shopify/order-created")]
    public async Task<IActionResult> ShopifyOrderCreated([FromBody] OrderCreateDto dto, CancellationToken ct)
    {
        var order = await _orders.CreateAsync(dto, ct);
        return Accepted(new { order.Id, order.Status });
    }

    [HttpPost("stripe/payment")]
    public async Task<IActionResult> Stripe([FromBody] StripePaymentEvent payload, CancellationToken ct)
    {
        var type = payload.Status?.Equals("succeeded", StringComparison.OrdinalIgnoreCase) == true
            ? EventTypes.PaymentSucceeded : EventTypes.PaymentFailed;
        await _bus.PublishAsync(DomainEvent.Create(type, "stripe", payload), ct);
        _logger.LogInformation("Stripe event {Type} for order {ShopOrderId}", type, payload.ShopOrderId);
        return Accepted();
    }

    public record StripePaymentEvent(string ShopOrderId, string Status, decimal Amount, string Currency);
}
