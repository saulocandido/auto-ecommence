using System.Text.Json;
using AutoCommerce.Shared.Contracts;

namespace AutoCommerce.ProductSelection.Services;

/// <summary>
/// Persists the latest scan/recommendation results in SQLite so that
/// GET /recommendations can return them without re-querying the AI provider.
/// </summary>
public interface IRecommendationCache
{
    Task SaveAsync(RecommendationResponse response, CancellationToken ct);
    Task<RecommendationResponse?> GetLatestAsync(CancellationToken ct);
}

public sealed class SqliteRecommendationCache : IRecommendationCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly ILogger<SqliteRecommendationCache> _logger;

    public SqliteRecommendationCache(string connectionString, ILogger<SqliteRecommendationCache> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
        EnsureSchema();
    }

    public async Task SaveAsync(RecommendationResponse response, CancellationToken ct)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO recommendation_cache (id, response_json, created_utc)
                VALUES (1, $json, $ts)
                ON CONFLICT(id) DO UPDATE SET
                    response_json = excluded.response_json,
                    created_utc   = excluded.created_utc;
                """;
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(response, JsonOptions));
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("Saved {Count} recommendations to cache", response.Recommendations.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save recommendation cache");
        }
    }

    public async Task<RecommendationResponse?> GetLatestAsync(CancellationToken ct)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT response_json FROM recommendation_cache WHERE id = 1 LIMIT 1;";
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is string json)
            {
                return JsonSerializer.Deserialize<RecommendationResponse>(json, JsonOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read recommendation cache");
        }
        return null;
    }

    private Microsoft.Data.Sqlite.SqliteConnection Open()
    {
        EnsureDataDirectory();
        var conn = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void EnsureDataDirectory()
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(_connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource == ":memory:") return;
        var dir = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
    }

    private void EnsureSchema()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS recommendation_cache (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    response_json TEXT NOT NULL,
                    created_utc TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create recommendation cache schema");
        }
    }
}
