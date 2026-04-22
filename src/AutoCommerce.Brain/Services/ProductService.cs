using AutoCommerce.Brain.Domain;
using AutoCommerce.Brain.Infrastructure;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;
using Microsoft.EntityFrameworkCore;

namespace AutoCommerce.Brain.Services;

public interface IProductService
{
    Task<ProductResponse> ImportAsync(ProductImportDto dto, CancellationToken ct);
    Task<ProductResponse?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<ProductResponse>> ListAsync(string? status, string? category, int skip, int take, CancellationToken ct);
    Task<ProductResponse?> UpdateAsync(Guid id, ProductUpdateDto dto, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    Task<ProductResponse?> AssignSupplierAsync(Guid id, SupplierAssignmentRequest request, CancellationToken ct);
}

public class ProductService : IProductService
{
    private readonly BrainDbContext _db;
    private readonly IEventBus _bus;
    private readonly IPricingEngine _pricing;
    private readonly ILogger<ProductService> _logger;

    public ProductService(BrainDbContext db, IEventBus bus, IPricingEngine pricing, ILogger<ProductService> logger)
    {
        _db = db;
        _bus = bus;
        _pricing = pricing;
        _logger = logger;
    }

    public async Task<ProductResponse> ImportAsync(ProductImportDto dto, CancellationToken ct)
    {
        var existing = await _db.Products.FirstOrDefaultAsync(p => p.ExternalId == dto.ExternalId, ct);
        var isNew = existing is null;

        var product = existing ?? new Product { ExternalId = dto.ExternalId };
        product.Title = dto.Title;
        product.Category = dto.Category;
        product.Description = dto.Description;
        product.ImageUrlsJson = JsonCols.Serialize(dto.ImageUrls);
        product.TagsJson = JsonCols.Serialize(dto.Tags);
        product.TargetMarket = dto.TargetMarket;
        product.Score = dto.Score;
        product.SuppliersJson = JsonCols.Serialize(dto.Suppliers);
        product.ScoreBreakdownJson = dto.ScoreBreakdown is null ? null : JsonCols.Serialize(dto.ScoreBreakdown);
        product.UpdatedAt = DateTimeOffset.UtcNow;

        var firstSupplier = dto.Suppliers.OrderBy(s => s.Cost).FirstOrDefault();
        if (firstSupplier is not null)
        {
            product.SupplierKey ??= firstSupplier.SupplierKey;
            product.Cost ??= firstSupplier.Cost;
        }

        if (isNew)
        {
            product.Status = ProductStatus.Draft;
            _db.Products.Add(product);
        }

        await _db.SaveChangesAsync(ct);

        if (product.Cost is > 0)
        {
            var priced = await _pricing.ApplyRuleAsync(product, ct);
            if (priced) await _db.SaveChangesAsync(ct);
        }

        await _bus.PublishAsync(DomainEvent.Create(EventTypes.ProductDiscovered, "brain",
            new { product.Id, product.ExternalId, product.Title, product.Category, product.Score }), ct);

        _logger.LogInformation("Imported product {ExternalId} ({Title})", product.ExternalId, product.Title);
        return ToResponse(product);
    }

    public async Task<ProductResponse?> GetAsync(Guid id, CancellationToken ct)
    {
        var p = await _db.Products.FindAsync(new object?[] { id }, ct);
        return p is null ? null : ToResponse(p);
    }

    public async Task<IReadOnlyList<ProductResponse>> ListAsync(string? status, string? category, int skip, int take, CancellationToken ct)
    {
        var q = _db.Products.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ProductStatus>(status, true, out var s))
            q = q.Where(p => p.Status == s);
        if (!string.IsNullOrWhiteSpace(category))
            q = q.Where(p => p.Category == category);
        var list = await q.ToListAsync(ct);
        return list
            .OrderByDescending(p => p.UpdatedAt)
            .Skip(skip)
            .Take(Math.Clamp(take, 1, 200))
            .Select(ToResponse)
            .ToList();
    }

    public async Task<ProductResponse?> UpdateAsync(Guid id, ProductUpdateDto dto, CancellationToken ct)
    {
        var p = await _db.Products.FindAsync(new object?[] { id }, ct);
        if (p is null) return null;

        if (dto.Title is not null) p.Title = dto.Title;
        if (dto.Description is not null) p.Description = dto.Description;
        if (dto.SupplierKey is not null) p.SupplierKey = dto.SupplierKey;
        if (dto.Cost.HasValue) p.Cost = dto.Cost.Value;
        if (dto.Price.HasValue) p.Price = dto.Price.Value;
        if (dto.Status is not null && Enum.TryParse<ProductStatus>(dto.Status, true, out var s))
        {
            var changed = p.Status != s;
            p.Status = s;
            p.UpdatedAt = DateTimeOffset.UtcNow;
            if (changed)
            {
                if (s == ProductStatus.Paused)
                    await _bus.PublishAsync(DomainEvent.Create(EventTypes.ProductPaused, "brain", new { p.Id }), ct);
                if (s == ProductStatus.Killed)
                    await _bus.PublishAsync(DomainEvent.Create(EventTypes.ProductKilled, "brain", new { p.Id }), ct);
                if (s == ProductStatus.Active)
                    await _bus.PublishAsync(DomainEvent.Create(EventTypes.ProductApproved, "brain", new { p.Id }), ct);
            }
        }

        p.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(p);
    }

    public async Task<ProductResponse?> AssignSupplierAsync(Guid id, SupplierAssignmentRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SupplierKey) || request.Cost <= 0) return null;

        var p = await _db.Products.FindAsync(new object?[] { id }, ct);
        if (p is null) return null;

        p.SupplierKey = request.SupplierKey;
        p.Cost = request.Cost;
        p.UpdatedAt = DateTimeOffset.UtcNow;

        var priced = await _pricing.ApplyRuleAsync(p, ct);
        await _db.SaveChangesAsync(ct);

        await _bus.PublishAsync(DomainEvent.Create(EventTypes.SupplierSelected, "brain",
            new SupplierSelectedPayload(
                p.Id, p.ExternalId, request.SupplierKey, request.Cost,
                request.Currency ?? "USD", request.Score, DateTimeOffset.UtcNow)), ct);

        _logger.LogInformation("Assigned supplier {Supplier} to product {ExternalId} at cost {Cost}",
            request.SupplierKey, p.ExternalId, request.Cost);

        return ToResponse(p);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var p = await _db.Products.FindAsync(new object?[] { id }, ct);
        if (p is null) return false;
        _db.Products.Remove(p);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    internal static ProductResponse ToResponse(Product p)
    {
        var images = JsonCols.Deserialize(p.ImageUrlsJson, new List<string>());
        var tags = JsonCols.Deserialize(p.TagsJson, new List<string>());
        var suppliers = JsonCols.Deserialize(p.SuppliersJson, new List<SupplierListing>());
        return new ProductResponse(
            p.Id, p.ExternalId, p.Title, p.Category, p.Description,
            images, tags, p.TargetMarket, p.Score, p.Cost, p.Price, p.MarginPercent,
            p.Status.ToString(), p.SupplierKey, suppliers, p.CreatedAt, p.UpdatedAt);
    }
}
