using Microsoft.EntityFrameworkCore;
using Serilog;
using AutoCommerce.StoreManagement.Infrastructure;
using AutoCommerce.StoreManagement.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) =>
    config.WriteTo.Console().MinimumLevel.Information());

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

builder.Services.AddControllers();

var brainUrl = builder.Configuration["Brain:BaseUrl"]
    ?? builder.Configuration["BrainUrl"]
    ?? "http://brain:8080";
var brainApiKey = builder.Configuration["Brain:ApiKey"]
    ?? builder.Configuration["Brain__ApiKey"]
    ?? "dev-master-key-change-me";
builder.Services.AddHttpClient<IBrainClient, BrainClient>(client =>
{
    client.BaseAddress = new Uri(brainUrl);
    client.DefaultRequestHeaders.Add("X-Api-Key", brainApiKey);
});

builder.Services.AddSingleton<IShopifyClient, MockShopifyClient>();
builder.Services.AddSingleton<IShopifyMetrics, ShopifyMetrics>();
builder.Services.AddSingleton<IRetryPolicy>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<ExponentialBackoffRetryPolicy>>();
    var cfg = sp.GetRequiredService<IConfiguration>();
    var attempts = int.TryParse(cfg["Shopify:MaxRetryAttempts"], out var a) ? a : 5;
    var delay = int.TryParse(cfg["Shopify:RetryBaseDelayMs"], out var d) ? d : 500;
    return new ExponentialBackoffRetryPolicy(logger, attempts, delay);
});

builder.Services.AddScoped<IStoreService, StoreService>();
builder.Services.AddScoped<IShopifySyncService, ShopifySyncService>();
builder.Services.AddSingleton<IProductMatchingEngine, ProductMatchingEngine>();
builder.Services.AddHttpClient();
// Session manager owns the Playwright storage state (cookies / origin storage) that
// the admin app client reuses across runs.
builder.Services.AddSingleton<IShopifySessionManager, ShopifySessionManager>();
// HTTP client kept registered for diagnostic/debug use, but the dropshipper-ai admin app
// does not expose a public JSON API — the real flow requires browser automation.
builder.Services.AddSingleton<HttpShopifyAdminAppClient>();
builder.Services.AddSingleton<PlaywrightShopifyAdminAppClient>();
builder.Services.AddSingleton<IShopifyAdminAppClient>(sp =>
{
    var usePlaywright = builder.Configuration.GetValue("Automation:UsePlaywright", true);
    return usePlaywright
        ? sp.GetRequiredService<PlaywrightShopifyAdminAppClient>()
        : sp.GetRequiredService<HttpShopifyAdminAppClient>();
});
builder.Services.AddScoped<IShopifyAutomationService, ShopifyAutomationService>();

builder.Services.AddDbContext<StoreDbContext>(options =>
    options.UseSqlite("Data Source=/app/data/store.db"));

builder.Services.AddHostedService<BrainEventSubscriber>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
    db.Database.EnsureCreated();

    // Add new columns to existing tables (EnsureCreated won't alter existing tables)
    foreach (var col in new[] { ("AutomationConfigs", "ShopifyEmail", "TEXT"), ("AutomationConfigs", "ShopifyPassword", "TEXT") })
    {
        try { db.Database.ExecuteSqlRaw($"ALTER TABLE {col.Item1} ADD COLUMN {col.Item2} {col.Item3}"); }
        catch { /* column already exists */ }
    }

    // One-shot: reset MaxRetries=3 (old default) to 0. User asked for single-attempt mode;
    // retries on real UI-automation failures usually just delay surfacing the real issue.
    try { db.Database.ExecuteSqlRaw("UPDATE AutomationConfigs SET MaxRetries = 0 WHERE MaxRetries = 3"); }
    catch { /* table may not exist yet */ }
}

app.UseHttpsRedirection();
app.UseCors();
app.MapControllers();

app.Run();

public partial class Program { }
