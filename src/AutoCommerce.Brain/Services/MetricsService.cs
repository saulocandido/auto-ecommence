using AutoCommerce.Brain.Domain;
using AutoCommerce.Brain.Infrastructure;
using AutoCommerce.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AutoCommerce.Brain.Services;

public interface IMetricsService
{
    Task<DashboardMetrics> GetDashboardAsync(CancellationToken ct);
}

public class MetricsService : IMetricsService
{
    private readonly BrainDbContext _db;
    public MetricsService(BrainDbContext db) => _db = db;

    public async Task<DashboardMetrics> GetDashboardAsync(CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-24);

        var productCounts = await _db.Products
            .GroupBy(p => p.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int count(ProductStatus s) => productCounts.FirstOrDefault(x => x.Key == s)?.Count ?? 0;

        var orderCounts = await _db.Orders
            .GroupBy(o => o.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int ocount(OrderStatus s) => orderCounts.FirstOrDefault(x => x.Key == s)?.Count ?? 0;

        var recent = await _db.Orders.Include(o => o.Lines)
            .Where(o => o.CreatedAt >= since)
            .ToListAsync(ct);
        var revenue = recent.Sum(o => o.Total);

        var productCostMap = await _db.Products
            .Where(p => p.Cost != null)
            .Select(p => new { p.Id, Cost = p.Cost!.Value })
            .ToListAsync(ct);
        var costLookup = productCostMap.ToDictionary(x => x.Id, x => x.Cost);
        decimal cogs = 0m;
        foreach (var o in recent)
            foreach (var line in o.Lines)
                if (costLookup.TryGetValue(line.ProductId, out var c))
                    cogs += c * line.Quantity;
        var profit = revenue - cogs;

        var totalProducts = await _db.Products.CountAsync(ct);
        var activeProducts = await _db.Products.Where(p => p.Price != null && p.Cost != null).ToListAsync(ct);
        var avgMargin = activeProducts.Count == 0
            ? 0.0
            : activeProducts.Average(p => (double)((p.Price!.Value - p.Cost!.Value) / p.Price.Value) * 100.0);

        var topProductsQuery = await _db.OrderLines
            .Join(_db.Products, l => l.ProductId, p => p.Id, (l, p) => new { l, p })
            .GroupBy(x => new { x.p.Id, x.p.Title })
            .Select(g => new
            {
                g.Key.Id,
                g.Key.Title,
                OrderCount = g.Count(),
                Revenue = g.Sum(x => x.l.UnitPrice * x.l.Quantity)
            })
            .OrderByDescending(x => x.Revenue)
            .Take(5)
            .ToListAsync(ct);

        var recentEvents = await _db.EventLogs
            .OrderByDescending(e => e.OccurredAt)
            .Take(20)
            .Select(e => new RecentEvent(e.Id, e.Type, e.Source, e.OccurredAt))
            .ToListAsync(ct);

        return new DashboardMetrics(
            TotalProducts: totalProducts,
            ActiveProducts: count(ProductStatus.Active),
            PausedProducts: count(ProductStatus.Paused),
            TotalOrders: orderCounts.Sum(x => x.Count),
            PendingOrders: ocount(OrderStatus.Pending),
            FulfilledOrders: ocount(OrderStatus.Fulfilled),
            RevenueLast24h: revenue,
            ProfitLast24h: profit,
            AvgMarginPercent: Math.Round(avgMargin, 2),
            TopProducts: topProductsQuery.Select(x => new TopProduct(x.Id, x.Title, x.OrderCount, x.Revenue)).ToList(),
            RecentEvents: recentEvents);
    }
}
