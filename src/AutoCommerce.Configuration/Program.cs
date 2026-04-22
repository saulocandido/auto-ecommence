using AutoCommerce.Configuration.Services;
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

// --- Settings store (SQLite) ---
var dbPath = builder.Configuration["ConfigurationSettings:DatabasePath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "configuration-settings.db");
var connectionString = builder.Configuration.GetConnectionString("Configuration")
    ?? new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
    {
        DataSource = dbPath,
        Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate
    }.ToString();

var defaultSettings = ConfigurationSettingsDefaults.Build(builder.Configuration);

builder.Services.AddSingleton<IConfigurationSettingsStore>(sp =>
    new SqliteConfigurationSettingsStore(
        connectionString,
        defaultSettings,
        sp.GetRequiredService<ILogger<SqliteConfigurationSettingsStore>>()));

// --- Subscriber notification service ---
var subscriberUrls = builder.Configuration.GetSection("Subscribers").Get<string[]>()
    ?? Array.Empty<string>();
builder.Services.AddHttpClient<IConfigurationNotifier, HttpConfigurationNotifier>();
builder.Services.AddSingleton<IConfigurationNotifier>(sp =>
    new HttpConfigurationNotifier(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(HttpConfigurationNotifier)),
        subscriberUrls,
        sp.GetRequiredService<ILogger<HttpConfigurationNotifier>>()));

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseSwagger();
app.UseSwaggerUI();
app.MapControllers();

app.Run();
