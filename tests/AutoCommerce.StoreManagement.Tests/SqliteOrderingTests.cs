using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FluentAssertions;
using AutoCommerce.StoreManagement.Domain;
using AutoCommerce.StoreManagement.Infrastructure;
using AutoCommerce.StoreManagement.Services;

namespace AutoCommerce.StoreManagement.Tests;

/// <summary>
/// These tests MUST run against real SQLite, not the EF in-memory provider, because the
/// bug being guarded against (<c>System.NotSupportedException: SQLite does not support
/// expressions of type 'DateTimeOffset' in ORDER BY clauses</c>) is specific to the
/// SQLite query-translator. The fix is a global
/// <see cref="Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter"/>
/// configured in <see cref="StoreDbContext.ConfigureConventions"/>.
/// </summary>
public class SqliteOrderingTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private ServiceProvider _sp = null!;

    public Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sqlite-order-{Guid.NewGuid()}.db");
        var services = new ServiceCollection();
        services.AddDbContext<StoreDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddLogging();
        _sp = services.BuildServiceProvider();

        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<StoreDbContext>().Database.EnsureCreated();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _sp.Dispose();
        try { File.Delete(_dbPath); } catch { /* ignore */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AutomationLogs_OrderByDescendingTimestamp_TranslatesOnSqlite()
    {
        var runId = Guid.NewGuid();
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            db.AutomationRuns.Add(new ShopifyAutomationRun { Id = runId, Status = "Running" });
            var t0 = DateTimeOffset.UtcNow;
            for (int i = 0; i < 5; i++)
                db.AutomationLogs.Add(new ShopifyAutomationLog
                {
                    RunId = runId,
                    Level = "info",
                    Message = $"log {i}",
                    Timestamp = t0.AddSeconds(i)
                });
            await db.SaveChangesAsync();
        }

        // This exact LINQ shape used to throw SqliteQueryableMethodTranslatingExpressionVisitor
        // "SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY clauses".
        List<ShopifyAutomationLog> logs;
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            logs = await db.AutomationLogs
                .Where(l => l.RunId == runId)
                .OrderByDescending(l => l.Timestamp)
                .Take(3)
                .ToListAsync();
        }

        logs.Should().HaveCount(3);
        logs[0].Message.Should().Be("log 4");
        logs[1].Message.Should().Be("log 3");
        logs[2].Message.Should().Be("log 2");
    }

    [Fact]
    public async Task AutomationProducts_OrderByCreatedAt_TranslatesOnSqlite()
    {
        var runId = Guid.NewGuid();
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            db.AutomationRuns.Add(new ShopifyAutomationRun { Id = runId, Status = "Running" });
            var t0 = DateTimeOffset.UtcNow;
            db.AutomationProducts.Add(new ShopifyAutomationProduct { RunId = runId, BrainProductId = Guid.NewGuid(), ProductName = "B", CreatedAt = t0.AddSeconds(2) });
            db.AutomationProducts.Add(new ShopifyAutomationProduct { RunId = runId, BrainProductId = Guid.NewGuid(), ProductName = "A", CreatedAt = t0.AddSeconds(1) });
            db.AutomationProducts.Add(new ShopifyAutomationProduct { RunId = runId, BrainProductId = Guid.NewGuid(), ProductName = "C", CreatedAt = t0.AddSeconds(3) });
            await db.SaveChangesAsync();
        }

        List<ShopifyAutomationProduct> ordered;
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            ordered = await db.AutomationProducts
                .Where(p => p.RunId == runId)
                .OrderBy(p => p.CreatedAt)
                .ToListAsync();
        }

        ordered.Select(p => p.ProductName).Should().Equal("A", "B", "C");
    }

    [Fact]
    public async Task AutomationRuns_OrderByStartedAtDescending_TranslatesOnSqlite()
    {
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var t0 = DateTimeOffset.UtcNow;
            db.AutomationRuns.Add(new ShopifyAutomationRun { Status = "Completed", StartedAt = t0.AddMinutes(-5) });
            db.AutomationRuns.Add(new ShopifyAutomationRun { Status = "Running", StartedAt = t0 });
            db.AutomationRuns.Add(new ShopifyAutomationRun { Status = "Failed", StartedAt = t0.AddMinutes(-10) });
            await db.SaveChangesAsync();
        }

        List<ShopifyAutomationRun> ordered;
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            ordered = await db.AutomationRuns
                .OrderByDescending(r => r.StartedAt)
                .Take(10)
                .ToListAsync();
        }

        ordered.Select(r => r.Status).Should().Equal("Running", "Completed", "Failed");
    }

    // Service-level assertion: the DTO endpoints used by the UI must now succeed on SQLite.
    [Fact]
    public async Task ShopifyAutomationService_GetRunProductsAndLogs_WorkOnSqlite()
    {
        var runId = Guid.NewGuid();
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            db.AutomationRuns.Add(new ShopifyAutomationRun { Id = runId, Status = "Running" });
            db.AutomationProducts.Add(new ShopifyAutomationProduct { RunId = runId, BrainProductId = Guid.NewGuid(), ProductName = "Alpha" });
            db.AutomationLogs.Add(new ShopifyAutomationLog { RunId = runId, Level = "info", Message = "hello" });
            await db.SaveChangesAsync();
        }

        var svc = new ShopifyAutomationService(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            new StubBrainClient(),
            new ProductMatchingEngine(),
            new StubAdminAppClient(),
            NullLogger<ShopifyAutomationService>.Instance);

        var products = await svc.GetRunProductsAsync(runId, CancellationToken.None);
        var logs = await svc.GetRunLogsAsync(runId, 100, CancellationToken.None);
        var metrics = await svc.GetMetricsAsync(runId, CancellationToken.None);

        products.Should().HaveCount(1);
        logs.Should().HaveCount(1);
        metrics.Total.Should().Be(1);
    }

    // Minimal stub so the test doesn't need Playwright / HTTP.
    private sealed class StubAdminAppClient : IShopifyAdminAppClient
    {
        public Task<IReadOnlyList<AdminAppSearchCandidate>> SearchAsync(ShopifyAutomationConfig c, string q, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AdminAppSearchCandidate>>(Array.Empty<AdminAppSearchCandidate>());
        public Task<string> AddToImportListAsync(ShopifyAutomationConfig c, string externalId, CancellationToken ct) =>
            Task.FromResult(externalId);
        public Task<IReadOnlyList<AdminAppImportListItem>> GetImportListAsync(ShopifyAutomationConfig c, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AdminAppImportListItem>>(Array.Empty<AdminAppImportListItem>());
        public Task<AdminAppPushResult> PushToStoreAsync(ShopifyAutomationConfig c, string importItemId, CancellationToken ct) =>
            Task.FromResult(new AdminAppPushResult(true, null, null));
    }
}
