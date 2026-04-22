using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AutoCommerce.Shared.Contracts;
using AutoCommerce.Shared.Events;
using AutoCommerce.StoreManagement.Infrastructure;
using AutoCommerce.StoreManagement.Services;

namespace AutoCommerce.StoreManagement.Tests;

internal class StoreApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"TestStoreDb-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var dbDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<StoreDbContext>));
            if (dbDescriptor != null) services.Remove(dbDescriptor);

            services.AddDbContext<StoreDbContext>(options =>
                options.UseInMemoryDatabase(_dbName));

            services.RemoveAll<IShopifyClient>();
            services.AddSingleton<IShopifyClient, MockShopifyClient>();

            services.RemoveAll<IBrainClient>();
            services.AddSingleton<IBrainClient, StubBrainClient>();

            var hosted = services.Where(d => d.ImplementationType == typeof(AutoCommerce.StoreManagement.Services.BrainEventSubscriber)).ToList();
            foreach (var h in hosted) services.Remove(h);

            var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            db.Database.EnsureCreated();
        });
    }
}

internal class StubBrainClient : IBrainClient
{
    public Dictionary<Guid, ProductResponse> Products { get; } = new();
    public List<DomainEvent> Published { get; } = new();

    public Task<ProductResponse?> GetProductAsync(Guid productId, CancellationToken ct = default)
        => Task.FromResult(Products.TryGetValue(productId, out var p) ? p : null);

    public Task<List<ProductResponse>> GetProductsAsync(string? status, CancellationToken ct = default)
        => Task.FromResult(Products.Values.ToList());

    public Task PublishEventAsync(DomainEvent evt, CancellationToken ct = default)
    {
        Published.Add(evt);
        return Task.CompletedTask;
    }

    public Task<List<RecentEventWithPayload>> PollEventsAsync(string type, DateTimeOffset since, int take, CancellationToken ct = default)
        => Task.FromResult(new List<RecentEventWithPayload>());
}

internal static class ServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}
