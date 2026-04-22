using AutoCommerce.Brain.Infrastructure;
using AutoCommerce.Brain.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();
builder.Host.UseSerilog();

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "AutoCommerce Brain", Version = "v1" });
    c.AddSecurityDefinition(ApiKeyDefaults.Scheme, new OpenApiSecurityScheme
    {
        Name = ApiKeyDefaults.HeaderName,
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "Provide the X-Api-Key header"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = ApiKeyDefaults.Scheme } }] = Array.Empty<string>()
    });
});

var conn = builder.Configuration.GetConnectionString("Default")
           ?? "Data Source=autocommerce.db";
builder.Services.AddDbContext<BrainDbContext>(o => o.UseSqlite(conn));

builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<IPricingEngine, PricingEngine>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IMetricsService, MetricsService>();
builder.Services.AddHostedService<EventRecorder>();

builder.Services
    .AddAuthentication(ApiKeyDefaults.Scheme)
    .AddScheme<ApiKeyOptions, ApiKeyAuthHandler>(ApiKeyDefaults.Scheme, o =>
    {
        o.MasterKey = builder.Configuration["ApiKey:Master"] ?? "dev-master-key-change-me";
    });

builder.Services.AddAuthorization(o =>
    o.DefaultPolicy = new AuthorizationPolicyBuilder(ApiKeyDefaults.Scheme).RequireAuthenticatedUser().Build());

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(_ => true)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BrainDbContext>();
    db.Database.EnsureCreated();
}

app.UseSerilogRequestLogging();
app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

public partial class Program { }
