using AutoCommerce.Shared.Contracts;
using AutoCommerce.SupplierSelection.Domain;
using AutoCommerce.SupplierSelection.Evaluation;
using AutoCommerce.SupplierSelection.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();
builder.Host.UseSerilog();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<ISupplierCatalog, StaticSupplierCatalog>();
builder.Services.AddSingleton<ISupplierEvaluator, SupplierEvaluator>();
builder.Services.AddSingleton<ISupplierSelector, SupplierSelector>();

var selection = builder.Configuration.GetSection("Selection");
var defaults = new SupplierSelectionOptions(
    MinScore: selection.GetValue("MinScore", 40.0),
    MaxShippingDays: selection.GetValue("MaxShippingDays", 21),
    MinStock: selection.GetValue("MinStock", 10),
    TargetMarket: selection.GetValue("TargetMarket", "IE") ?? "IE");
builder.Services.AddSingleton(defaults);

var fulfillmentCfg = builder.Configuration.GetSection("Fulfillment");
builder.Services.AddSingleton(new FulfillmentOptions
{
    ForcedSuccessRate = fulfillmentCfg.GetValue<double?>("ForcedSuccessRate"),
    MinDeliveryDays = fulfillmentCfg.GetValue("MinDeliveryDays", 4),
    MaxDeliveryDays = fulfillmentCfg.GetValue("MaxDeliveryDays", 14),
    RandomSeed = fulfillmentCfg.GetValue("RandomSeed", 0)
});

var discoveryCfg = builder.Configuration.GetSection("DiscoveryWorker");
builder.Services.AddSingleton(new DiscoveredWorkerOptions
{
    Enabled = discoveryCfg.GetValue("Enabled", true),
    PollInterval = TimeSpan.FromSeconds(discoveryCfg.GetValue("IntervalSeconds", 30)),
    StartupDelay = TimeSpan.FromSeconds(discoveryCfg.GetValue("StartupDelaySeconds", 10))
});

var priceMonitorCfg = builder.Configuration.GetSection("PriceMonitor");
builder.Services.AddSingleton(new PriceMonitorOptions
{
    Enabled = priceMonitorCfg.GetValue("Enabled", false),
    PollInterval = TimeSpan.FromMinutes(priceMonitorCfg.GetValue("IntervalMinutes", 60)),
    StartupDelay = TimeSpan.FromSeconds(priceMonitorCfg.GetValue("StartupDelaySeconds", 60)),
    PriceChangeProbability = priceMonitorCfg.GetValue("ChangeProbability", 0.10),
    MaxChangePercent = priceMonitorCfg.GetValue("MaxChangePercent", 0.15),
    Seed = priceMonitorCfg.GetValue("Seed", 0)
});

builder.Services.AddScoped<ISelectionService, SelectionService>();
builder.Services.AddScoped<IFulfillmentService, FulfillmentService>();

var brainUrl = builder.Configuration["Brain:BaseUrl"] ?? "http://localhost:5080/";
var brainKey = builder.Configuration["Brain:ApiKey"] ?? "dev-master-key-change-me";
builder.Services.AddHttpClient<IBrainClient, BrainClient>(c =>
{
    c.BaseAddress = new Uri(brainUrl);
    c.DefaultRequestHeaders.Add("X-Api-Key", brainKey);
    c.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHostedService<ProductDiscoveredWorker>();
builder.Services.AddHostedService<SupplierPriceMonitor>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(_ => true)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.MapControllers();

app.Run();

public partial class Program { }
