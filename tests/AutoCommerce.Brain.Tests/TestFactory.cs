using AutoCommerce.Brain.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AutoCommerce.Brain.Tests;

public class BrainFactory : WebApplicationFactory<Program>
{
    public string MasterKey { get; } = $"test-{Guid.NewGuid():N}";
    private readonly string _dbName = $"brain-tests-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiKey:Master"] = MasterKey,
                ["ConnectionStrings:Default"] = "DataSource=:memory:"
            });
        });
        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<BrainDbContext>));
            if (descriptor is not null) services.Remove(descriptor);
            services.AddDbContext<BrainDbContext>(o => o.UseInMemoryDatabase(_dbName));
        });
    }

    public HttpClient CreateAuthedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", MasterKey);
        return client;
    }
}
