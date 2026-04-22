using AutoCommerce.ProductSelection.Crawlers;
using AutoCommerce.ProductSelection.Scoring;
using AutoCommerce.ProductSelection.Services;
using AutoCommerce.Shared.Contracts;
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

builder.Services.AddSingleton<IScoringEngine>(_ => new ScoringEngine());
builder.Services.AddSingleton<ICandidateFilter, TopNPerCategoryFilter>();

// --- Configuration: fetch from Configuration MS, cache locally ---
var configUrl = builder.Configuration["Configuration:BaseUrl"] ?? "http://configuration:8080/";

var openAi = builder.Configuration.GetSection("OpenAI");
var fallbackSettings = new RecommendationRuntimeSettings(
    new OpenAiRecommendationOptions
    {
        ApiKey = openAi["ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
        Model = openAi["Model"] ?? "gpt-5",
        ReasoningEffort = openAi["ReasoningEffort"] ?? "low",
        MaxCandidates = openAi.GetValue("MaxCandidates", 48),
        RequestTimeoutSeconds = openAi.GetValue("RequestTimeoutSeconds", 90)
    },
    new RecommendationCredentialValues(
        OpenAiApiKey: openAi["ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
        GeminiApiKey: builder.Configuration["Gemini:ApiKey"]
                      ?? builder.Configuration["Google:ApiKey"]
                      ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                      ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY"),
        AdditionalSecrets: Array.Empty<RecommendationNamedCredentialValue>()),
    new SelectionConfig(
        TargetCategories: Array.Empty<string>(),
        MinPrice: null, MaxPrice: null, MinScore: null,
        TopNPerCategory: 3, TargetMarket: "IE", MaxShippingDays: null));

builder.Services.AddSingleton<IRecommendationSettingsStore>(sp =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var client = httpFactory.CreateClient("ConfigurationMS");
    client.BaseAddress = new Uri(configUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
    return new RemoteConfigurationClient(
        client,
        fallbackSettings,
        sp.GetRequiredService<ILogger<RemoteConfigurationClient>>());
});

builder.Services.AddHttpClient("ConfigurationMS");

builder.Services.AddHttpClient<ICandidateSource, OpenAiRealtimeTrendSource>(c =>
{
    c.BaseAddress = new Uri("https://api.openai.com/");
});

var scannerOpts = new ScheduledScannerOptions
{
    Enabled = builder.Configuration.GetValue("Scanner:Enabled", true),
    Interval = TimeSpan.FromMinutes(builder.Configuration.GetValue("Scanner:IntervalMinutes", 360)),
    StartupDelay = TimeSpan.FromSeconds(builder.Configuration.GetValue("Scanner:StartupDelaySeconds", 15))
};
builder.Services.AddSingleton(scannerOpts);

builder.Services.AddSingleton<IRecommendationCache>(sp =>
{
    var cachePath = builder.Configuration["RecommendationCache:DatabasePath"]
        ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "recommendation-cache.db");
    var cacheConnStr = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
    {
        DataSource = cachePath,
        Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate
    }.ToString();
    return new SqliteRecommendationCache(cacheConnStr, sp.GetRequiredService<ILogger<SqliteRecommendationCache>>());
});

builder.Services.AddScoped<ISelectionOrchestrator, SelectionOrchestrator>();

var brainUrl = builder.Configuration["Brain:BaseUrl"] ?? "http://localhost:5080/";
var brainKey = builder.Configuration["Brain:ApiKey"] ?? "dev-master-key-change-me";
builder.Services.AddHttpClient<IBrainClient, BrainClient>(c =>
{
    c.BaseAddress = new Uri(brainUrl);
    c.DefaultRequestHeaders.Add("X-Api-Key", brainKey);
    c.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient<ILinkValidator, LinkValidator>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddHostedService<ScheduledScanner>();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseSwagger();
app.UseSwaggerUI();
app.MapControllers();

app.Run();

public partial class Program { }
