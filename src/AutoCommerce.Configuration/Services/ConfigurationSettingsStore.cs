using System.Text.Json;
using AutoCommerce.Shared.Contracts;
using Microsoft.Data.Sqlite;

namespace AutoCommerce.Configuration.Services;

// ── Records (same shape as ProductSelection used, kept as shared contract) ──

public sealed record ConfigurationProviderOptions
{
    public string? ApiKey { get; init; }
    public string Model { get; init; } = "gpt-5";
    public string ReasoningEffort { get; init; } = "low";
    public int MaxCandidates { get; init; } = 48;
    public int RequestTimeoutSeconds { get; init; } = 90;
}

public sealed record NamedCredentialValue(string Name, string Value);

public sealed record CredentialValues(
    string? OpenAiApiKey,
    string? GeminiApiKey,
    IReadOnlyList<NamedCredentialValue> AdditionalSecrets);

public sealed record RuntimeSettings(
    ConfigurationProviderOptions Provider,
    CredentialValues Credentials,
    SelectionConfig Selection);

// ── Response DTOs ──

public sealed record ProviderSettingsDto(
    bool HasApiKey,
    string Model,
    string ReasoningEffort,
    int MaxCandidates,
    int RequestTimeoutSeconds,
    string EffectiveProvider);

public sealed record NamedCredentialStatusDto(string Name, bool HasValue, string? Preview);

public sealed record CredentialsSettingsDto(
    bool HasOpenAiApiKey,
    bool HasGeminiApiKey,
    string? OpenAiApiKeyPreview,
    string? GeminiApiKeyPreview,
    IReadOnlyList<NamedCredentialStatusDto> AdditionalSecrets);

public sealed record ConfigurationSettingsResponse(
    ProviderSettingsDto Provider,
    CredentialsSettingsDto Credentials,
    SelectionConfig Selection);

// ── Update DTOs ──

public sealed record ProviderSettingsUpdateDto(
    string? ApiKey,
    bool ClearApiKey,
    string Model,
    string ReasoningEffort,
    int MaxCandidates,
    int RequestTimeoutSeconds);

public sealed record NamedCredentialUpdateDto(string Name, string? Value, bool Clear);

public sealed record CredentialsUpdateDto(
    string? OpenAiApiKey,
    bool ClearOpenAiApiKey,
    string? GeminiApiKey,
    bool ClearGeminiApiKey,
    IReadOnlyList<NamedCredentialUpdateDto> AdditionalSecrets);

public sealed record ConfigurationSettingsUpdate(
    ProviderSettingsUpdateDto Provider,
    CredentialsUpdateDto Credentials,
    SelectionConfig Selection);

// ── Internal-only full-settings response (includes actual secret values for MS-to-MS calls) ──

public sealed record InternalSettingsResponse(
    ConfigurationProviderOptions Provider,
    CredentialValues Credentials,
    SelectionConfig Selection);

// ── Interface ──

public interface IConfigurationSettingsStore
{
    RuntimeSettings GetCurrent();
    ConfigurationSettingsResponse GetResponse();
    InternalSettingsResponse GetInternal();
    Task<ConfigurationSettingsResponse> UpdateAsync(ConfigurationSettingsUpdate update, CancellationToken ct);
}

// ── Defaults builder ──

public static class ConfigurationSettingsDefaults
{
    public static RuntimeSettings Build(IConfiguration config)
    {
        var openAi = config.GetSection("OpenAI");
        var provider = new ConfigurationProviderOptions
        {
            ApiKey = openAi["ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            Model = openAi["Model"] ?? "gpt-5",
            ReasoningEffort = openAi["ReasoningEffort"] ?? "low",
            MaxCandidates = openAi.GetValue("MaxCandidates", 48),
            RequestTimeoutSeconds = openAi.GetValue("RequestTimeoutSeconds", 90)
        };

        var credentials = new CredentialValues(
            OpenAiApiKey: provider.ApiKey,
            GeminiApiKey: config["Gemini:ApiKey"]
                          ?? config["Google:ApiKey"]
                          ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                          ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY"),
            AdditionalSecrets: Array.Empty<NamedCredentialValue>());

        var selection = config.GetSection("Selection");
        var defaultConfig = new SelectionConfig(
            TargetCategories: selection.GetSection("TargetCategories").Get<string[]>() ?? Array.Empty<string>(),
            MinPrice: selection.GetValue<decimal?>("MinPrice", null),
            MaxPrice: selection.GetValue<decimal?>("MaxPrice", null),
            MinScore: selection.GetValue<double?>("MinScore", null),
            TopNPerCategory: selection.GetValue("TopNPerCategory", 3),
            TargetMarket: selection.GetValue("TargetMarket", "IE") ?? "IE",
            MaxShippingDays: selection.GetValue<int?>("MaxShippingDays", null));

        return new RuntimeSettings(provider, credentials, defaultConfig);
    }
}

// ── SQLite implementation ──

public sealed class SqliteConfigurationSettingsStore : IConfigurationSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly object _sync = new();
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly string _connectionString;
    private readonly ILogger<SqliteConfigurationSettingsStore> _logger;

    private RuntimeSettings _current;

    public SqliteConfigurationSettingsStore(
        string connectionString,
        RuntimeSettings defaults,
        ILogger<SqliteConfigurationSettingsStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
        _current = LoadInitial(Normalize(defaults));
    }

    public RuntimeSettings GetCurrent()
    {
        lock (_sync) return Clone(_current);
    }

    public ConfigurationSettingsResponse GetResponse() => ToResponse(GetCurrent());

    public InternalSettingsResponse GetInternal()
    {
        var c = GetCurrent();
        return new InternalSettingsResponse(c.Provider, c.Credentials, c.Selection);
    }

    public async Task<ConfigurationSettingsResponse> UpdateAsync(ConfigurationSettingsUpdate update, CancellationToken ct)
    {
        await _updateGate.WaitAsync(ct);
        try
        {
            RuntimeSettings current;
            lock (_sync) current = _current;

            var next = ApplyUpdate(current, update);
            await PersistAsync(next, ct);
            lock (_sync) _current = next;

            return ToResponse(next);
        }
        finally { _updateGate.Release(); }
    }

    // ── Persistence ──

    private RuntimeSettings LoadInitial(RuntimeSettings defaults)
    {
        try
        {
            using var conn = OpenConnection();
            EnsureSchema(conn);
            if (TryRead(conn, defaults, out var persisted)) return persisted;
            Persist(conn, defaults);
            return Clone(defaults);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load configuration settings from SQLite; using defaults");
            return Clone(defaults);
        }
    }

    private async Task PersistAsync(RuntimeSettings settings, CancellationToken ct)
    {
        using var conn = OpenConnection();
        EnsureSchema(conn);
        using var cmd = BuildUpsert(conn, settings);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private SqliteConnection OpenConnection()
    {
        var b = new SqliteConnectionStringBuilder(_connectionString);
        if (!string.IsNullOrWhiteSpace(b.DataSource) && b.DataSource != ":memory:")
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(b.DataSource));
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        }
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS configuration_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                provider_json TEXT NOT NULL,
                openai_api_key TEXT NULL,
                gemini_api_key TEXT NULL,
                additional_secrets_json TEXT NOT NULL,
                selection_json TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private bool TryRead(SqliteConnection conn, RuntimeSettings defaults, out RuntimeSettings result)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT provider_json, openai_api_key, gemini_api_key, additional_secrets_json, selection_json
            FROM configuration_settings WHERE id = 1 LIMIT 1;
            """;
        using var r = cmd.ExecuteReader();
        if (!r.Read()) { result = defaults; return false; }

        var provider = JsonSerializer.Deserialize<ConfigurationProviderOptions>(r.GetString(0), JsonOpts) ?? defaults.Provider;
        var selection = JsonSerializer.Deserialize<SelectionConfig>(r.GetString(4), JsonOpts) ?? defaults.Selection;
        var extras = JsonSerializer.Deserialize<NamedCredentialValue[]>(r.GetString(3), JsonOpts) ?? Array.Empty<NamedCredentialValue>();
        var openAi = r.IsDBNull(1) ? provider.ApiKey ?? defaults.Credentials.OpenAiApiKey : r.GetString(1);
        var gemini = r.IsDBNull(2) ? defaults.Credentials.GeminiApiKey : r.GetString(2);

        result = Normalize(new RuntimeSettings(provider, new CredentialValues(openAi, gemini, extras), selection));
        return true;
    }

    private static void Persist(SqliteConnection conn, RuntimeSettings s)
    {
        using var cmd = BuildUpsert(conn, s);
        cmd.ExecuteNonQuery();
    }

    private static SqliteCommand BuildUpsert(SqliteConnection conn, RuntimeSettings s)
    {
        var n = Normalize(s);
        var providerForJson = new ConfigurationProviderOptions
        {
            ApiKey = null,
            Model = n.Provider.Model,
            ReasoningEffort = n.Provider.ReasoningEffort,
            MaxCandidates = n.Provider.MaxCandidates,
            RequestTimeoutSeconds = n.Provider.RequestTimeoutSeconds
        };

        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO configuration_settings (id, provider_json, openai_api_key, gemini_api_key, additional_secrets_json, selection_json, updated_utc)
            VALUES (1, $pj, $oak, $gak, $asj, $sj, $u)
            ON CONFLICT(id) DO UPDATE SET
                provider_json = excluded.provider_json,
                openai_api_key = excluded.openai_api_key,
                gemini_api_key = excluded.gemini_api_key,
                additional_secrets_json = excluded.additional_secrets_json,
                selection_json = excluded.selection_json,
                updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$pj", JsonSerializer.Serialize(providerForJson, JsonOpts));
        cmd.Parameters.AddWithValue("$oak", (object?)n.Credentials.OpenAiApiKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gak", (object?)n.Credentials.GeminiApiKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$asj", JsonSerializer.Serialize(n.Credentials.AdditionalSecrets, JsonOpts));
        cmd.Parameters.AddWithValue("$sj", JsonSerializer.Serialize(n.Selection, JsonOpts));
        cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToString("O"));
        return cmd;
    }

    // ── Mapping ──

    private static RuntimeSettings ApplyUpdate(RuntimeSettings current, ConfigurationSettingsUpdate update)
    {
        var provider = new ConfigurationProviderOptions
        {
            Model = update.Provider.Model,
            ReasoningEffort = update.Provider.ReasoningEffort,
            MaxCandidates = update.Provider.MaxCandidates,
            RequestTimeoutSeconds = update.Provider.RequestTimeoutSeconds
        };

        var openAi = Resolve(update.Credentials.ClearOpenAiApiKey || update.Provider.ClearApiKey,
            update.Credentials.OpenAiApiKey ?? update.Provider.ApiKey, current.Credentials.OpenAiApiKey);
        var gemini = Resolve(update.Credentials.ClearGeminiApiKey,
            update.Credentials.GeminiApiKey, current.Credentials.GeminiApiKey);

        var curExtras = current.Credentials.AdditionalSecrets
            .ToDictionary(s => s.Name, s => s.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var s in update.Credentials.AdditionalSecrets)
        {
            if (s.Clear) { curExtras.Remove(s.Name); continue; }
            if (!string.IsNullOrWhiteSpace(s.Value)) curExtras[s.Name] = s.Value.Trim();
        }

        var creds = new CredentialValues(openAi, gemini,
            curExtras.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                .Select(e => new NamedCredentialValue(e.Key, e.Value)).ToArray());

        return Normalize(new RuntimeSettings(provider, creds, update.Selection));
    }

    private static string? Resolve(bool clear, string? incoming, string? current)
        => clear ? null : string.IsNullOrWhiteSpace(incoming) ? current : incoming.Trim();

    private static ConfigurationSettingsResponse ToResponse(RuntimeSettings s) => new(
        new ProviderSettingsDto(
            HasApiKey: !string.IsNullOrWhiteSpace(s.Credentials.OpenAiApiKey),
            Model: s.Provider.Model,
            ReasoningEffort: s.Provider.ReasoningEffort,
            MaxCandidates: s.Provider.MaxCandidates,
            RequestTimeoutSeconds: s.Provider.RequestTimeoutSeconds,
            EffectiveProvider: EffectiveProvider(s)),
        new CredentialsSettingsDto(
            HasOpenAiApiKey: !string.IsNullOrWhiteSpace(s.Credentials.OpenAiApiKey),
            HasGeminiApiKey: !string.IsNullOrWhiteSpace(s.Credentials.GeminiApiKey),
            OpenAiApiKeyPreview: Preview(s.Credentials.OpenAiApiKey),
            GeminiApiKeyPreview: Preview(s.Credentials.GeminiApiKey),
            AdditionalSecrets: s.Credentials.AdditionalSecrets
                .Select(x => new NamedCredentialStatusDto(x.Name, !string.IsNullOrWhiteSpace(x.Value), Preview(x.Value)))
                .ToArray()),
        s.Selection);

    private static string EffectiveProvider(RuntimeSettings s)
    {
        var model = s.Provider.Model?.Trim();
        var hasO = !string.IsNullOrWhiteSpace(s.Credentials.OpenAiApiKey);
        var hasG = !string.IsNullOrWhiteSpace(s.Credentials.GeminiApiKey);
        var prefersG = !string.IsNullOrWhiteSpace(model) && model.StartsWith("gemini", StringComparison.OrdinalIgnoreCase);
        if (hasG && (!hasO || prefersG)) return "Gemini";
        if (hasO) return "OpenAI";
        return "None";
    }

    private static string? Preview(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var t = v.Trim();
        return t.Length <= 4 ? new string('*', t.Length) : $"****{t[^4..]}";
    }

    private static RuntimeSettings Normalize(RuntimeSettings s)
    {
        var creds = NormalizeCreds(s.Credentials);
        return new RuntimeSettings(
            new ConfigurationProviderOptions
            {
                ApiKey = creds.OpenAiApiKey,
                Model = string.IsNullOrWhiteSpace(s.Provider.Model) ? "gpt-5" : s.Provider.Model.Trim(),
                ReasoningEffort = string.IsNullOrWhiteSpace(s.Provider.ReasoningEffort) ? "low" : s.Provider.ReasoningEffort.Trim().ToLowerInvariant(),
                MaxCandidates = s.Provider.MaxCandidates < 1 ? 48 : s.Provider.MaxCandidates,
                RequestTimeoutSeconds = s.Provider.RequestTimeoutSeconds < 5 ? 90 : s.Provider.RequestTimeoutSeconds
            },
            creds,
            new SelectionConfig(
                TargetCategories: (s.Selection.TargetCategories ?? Array.Empty<string>())
                    .Select(c => c?.Trim()).Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Cast<string>().ToArray(),
                MinPrice: s.Selection.MinPrice,
                MaxPrice: s.Selection.MaxPrice,
                MinScore: s.Selection.MinScore,
                TopNPerCategory: s.Selection.TopNPerCategory,
                TargetMarket: (s.Selection.TargetMarket ?? "IE").Trim().ToUpperInvariant(),
                MaxShippingDays: s.Selection.MaxShippingDays));
    }

    private static CredentialValues NormalizeCreds(CredentialValues c) => new(
        OpenAiApiKey: string.IsNullOrWhiteSpace(c.OpenAiApiKey) ? null : c.OpenAiApiKey.Trim(),
        GeminiApiKey: string.IsNullOrWhiteSpace(c.GeminiApiKey) ? null : c.GeminiApiKey.Trim(),
        AdditionalSecrets: (c.AdditionalSecrets ?? Array.Empty<NamedCredentialValue>())
            .Where(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Value))
            .GroupBy(s => s.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new NamedCredentialValue(g.Key, g.Last().Value.Trim()))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray());

    private static RuntimeSettings Clone(RuntimeSettings s) => new(
        new ConfigurationProviderOptions
        {
            ApiKey = s.Provider.ApiKey,
            Model = s.Provider.Model,
            ReasoningEffort = s.Provider.ReasoningEffort,
            MaxCandidates = s.Provider.MaxCandidates,
            RequestTimeoutSeconds = s.Provider.RequestTimeoutSeconds
        },
        new CredentialValues(s.Credentials.OpenAiApiKey, s.Credentials.GeminiApiKey,
            s.Credentials.AdditionalSecrets.Select(x => new NamedCredentialValue(x.Name, x.Value)).ToArray()),
        s.Selection);
}
