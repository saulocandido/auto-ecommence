using AutoCommerce.Shared.Contracts;
using AutoCommerce.SupplierSelection.Domain;
using Microsoft.AspNetCore.Mvc;

namespace AutoCommerce.SupplierSelection.Controllers;

[ApiController]
[Route("suppliers")]
public class SuppliersController : ControllerBase
{
    private readonly ISupplierCatalog _catalog;
    public SuppliersController(ISupplierCatalog catalog) => _catalog = catalog;

    [HttpGet]
    public IReadOnlyList<SupplierProfile> List() => _catalog.All();

    [HttpGet("{key}")]
    public ActionResult<SupplierProfile> Get(string key)
    {
        var p = _catalog.Get(key);
        return p is null ? NotFound() : p;
    }
}
