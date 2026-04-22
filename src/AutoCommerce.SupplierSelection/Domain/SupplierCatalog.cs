using AutoCommerce.Shared.Contracts;

namespace AutoCommerce.SupplierSelection.Domain;

public interface ISupplierCatalog
{
    IReadOnlyList<SupplierProfile> All();
    SupplierProfile? Get(string supplierKey);
}

public class StaticSupplierCatalog : ISupplierCatalog
{
    private readonly Dictionary<string, SupplierProfile> _profiles;

    public StaticSupplierCatalog(IEnumerable<SupplierProfile>? profiles = null)
    {
        var seed = profiles?.ToList();
        if (seed is null || seed.Count == 0) seed = DefaultSeed();
        _profiles = seed.ToDictionary(p => p.SupplierKey, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<SupplierProfile> All() => _profiles.Values.ToList();

    public SupplierProfile? Get(string supplierKey) =>
        _profiles.TryGetValue(supplierKey, out var p) ? p : null;

    private static List<SupplierProfile> DefaultSeed() => new()
    {
        new("aliexpress", "AliExpress Standard", "CN", 0.62, "Large catalog, variable shipping"),
        new("aliexpress-choice", "AliExpress Choice", "CN", 0.78, "Faster dispatch, better QA"),
        new("cjdropshipping", "CJ Dropshipping", "CN", 0.74, "Warehouses in US/EU, steady"),
        new("spocket", "Spocket", "EU", 0.85, "US/EU suppliers, fast, higher cost"),
        new("zendrop", "Zendrop", "US", 0.82, "US fulfillment, branded invoices"),
        new("bigbuy", "BigBuy", "EU", 0.80, "EU warehouse, fast for IE/UK"),
        new("printful", "Printful", "US", 0.88, "Print on demand, reliable"),
        new("amazon-prime", "Amazon Prime", "US", 0.90, "Premium reliability, tighter margins")
    };
}
