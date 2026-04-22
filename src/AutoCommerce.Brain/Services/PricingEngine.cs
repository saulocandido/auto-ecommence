using AutoCommerce.Brain.Domain;
using AutoCommerce.Brain.Infrastructure;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;
using Microsoft.EntityFrameworkCore;

namespace AutoCommerce.Brain.Services;

public interface IPricingEngine
{
    Task<bool> ApplyRuleAsync(Product product, CancellationToken ct);
    Task<PricingRuleDto> UpsertRuleAsync(PricingRuleDto rule, CancellationToken ct);
    Task<IReadOnlyList<PricingRuleDto>> ListRulesAsync(CancellationToken ct);
    Task<PriceUpdateDto?> SetPriceAsync(Guid productId, decimal price, CancellationToken ct);
    Task HandleSupplierPriceChangeAsync(Guid productId, decimal newCost, CancellationToken ct);
}

public class PricingEngine : IPricingEngine
{
    private readonly BrainDbContext _db;
    private readonly IEventBus _bus;
    private readonly ILogger<PricingEngine> _logger;

    public PricingEngine(BrainDbContext db, IEventBus bus, ILogger<PricingEngine> logger)
    {
        _db = db;
        _bus = bus;
        _logger = logger;
    }

    public async Task<bool> ApplyRuleAsync(Product product, CancellationToken ct)
    {
        if (product.Cost is not { } cost || cost <= 0) return false;
        var rule = await _db.PricingRules.FirstOrDefaultAsync(r => r.Category == product.Category, ct)
                   ?? await _db.PricingRules.FirstOrDefaultAsync(r => r.Category == "*", ct)
                   ?? Default();

        var recommended = decimal.Round(cost * (decimal)rule.MarkupMultiplier, 2);
        recommended = Math.Clamp(recommended, rule.MinPrice, rule.MaxPrice);
        var margin = recommended > 0 ? (double)((recommended - cost) / recommended) * 100.0 : 0;

        product.Price = recommended;
        product.UpdatedAt = DateTimeOffset.UtcNow;

        if (margin < rule.MinMarginPercent)
        {
            product.Status = ProductStatus.Paused;
            await _bus.PublishAsync(DomainEvent.Create(EventTypes.MarginAlert, "pricing-engine",
                new { product.Id, product.ExternalId, cost, price = recommended, margin, threshold = rule.MinMarginPercent }), ct);
            _logger.LogWarning("Margin {Margin:F1}% below threshold {Threshold}% for {ExternalId}; paused.",
                margin, rule.MinMarginPercent, product.ExternalId);
        }
        else if (product.Status == ProductStatus.Draft)
        {
            product.Status = ProductStatus.Active;
        }

        await _bus.PublishAsync(DomainEvent.Create(EventTypes.PriceUpdated, "pricing-engine",
            new PriceUpdateDto(product.Id, cost, recommended, margin)), ct);
        return true;
    }

    public async Task<PricingRuleDto> UpsertRuleAsync(PricingRuleDto rule, CancellationToken ct)
    {
        var existing = await _db.PricingRules.FirstOrDefaultAsync(r => r.Category == rule.Category, ct);
        if (existing is null)
        {
            existing = new PricingRule { Category = rule.Category };
            _db.PricingRules.Add(existing);
        }
        existing.MarkupMultiplier = rule.MarkupMultiplier;
        existing.MinMarginPercent = rule.MinMarginPercent;
        existing.MinPrice = rule.MinPrice;
        existing.MaxPrice = rule.MaxPrice;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task<IReadOnlyList<PricingRuleDto>> ListRulesAsync(CancellationToken ct)
    {
        var rules = await _db.PricingRules.AsNoTracking().ToListAsync(ct);
        return rules.Select(r => new PricingRuleDto(r.Category, r.MarkupMultiplier, r.MinMarginPercent, r.MinPrice, r.MaxPrice)).ToList();
    }

    public async Task<PriceUpdateDto?> SetPriceAsync(Guid productId, decimal price, CancellationToken ct)
    {
        var p = await _db.Products.FindAsync(new object?[] { productId }, ct);
        if (p is null) return null;
        p.Price = price;
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        var margin = (p.Cost is > 0 && price > 0) ? (double)((price - p.Cost.Value) / price) * 100.0 : 0;
        var evt = new PriceUpdateDto(p.Id, p.Cost ?? 0, price, margin);
        await _bus.PublishAsync(DomainEvent.Create(EventTypes.PriceUpdated, "pricing-engine", evt), ct);
        return evt;
    }

    public async Task HandleSupplierPriceChangeAsync(Guid productId, decimal newCost, CancellationToken ct)
    {
        var p = await _db.Products.FindAsync(new object?[] { productId }, ct);
        if (p is null) return;
        p.Cost = newCost;
        await ApplyRuleAsync(p, ct);
        await _db.SaveChangesAsync(ct);
    }

    private static PricingRule Default() => new()
    {
        Category = "*",
        MarkupMultiplier = 2.5,
        MinMarginPercent = 25.0,
        MinPrice = 5m,
        MaxPrice = 500m
    };
}
