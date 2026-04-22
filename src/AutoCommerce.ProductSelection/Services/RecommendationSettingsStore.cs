using System.Text.Json;
using AutoCommerce.ProductSelection.Crawlers;
using AutoCommerce.Shared.Contracts;
using Microsoft.Data.Sqlite;

namespace AutoCommerce.ProductSelection.Services;

public sealed record RecommendationRuntimeSettings(
    OpenAiRecommendationOptions Provider,
    RecommendationCredentialValues Credentials,
    SelectionConfig Selection);

public sealed record RecommendationProviderSettings(
    bool HasApiKey,
    string Model,
    string ReasoningEffort,
    int MaxCandidates,
    int RequestTimeoutSeconds,
    string EffectiveProvider);

public sealed record RecommendationCredentialValues(
    string? OpenAiApiKey,
    string? GeminiApiKey,
    IReadOnlyList<RecommendationNamedCredentialValue> AdditionalSecrets);

public sealed record RecommendationNamedCredentialValue(
    string Name,
    string Value);

public sealed record RecommendationCredentialsSettings(
    bool HasOpenAiApiKey,
    bool HasGeminiApiKey,
    string? OpenAiApiKeyPreview,
    string? GeminiApiKeyPreview,
    IReadOnlyList<RecommendationNamedCredentialStatus> AdditionalSecrets);

public sealed record RecommendationNamedCredentialStatus(
    string Name,
    bool HasValue,
    string? Preview);

public sealed record RecommendationSettingsResponse(
    RecommendationProviderSettings Provider,
    RecommendationCredentialsSettings Credentials,
    SelectionConfig Selection);

public sealed record RecommendationProviderSettingsUpdate(
    string? ApiKey,
    bool ClearApiKey,
    string Model,
    string ReasoningEffort,
    int MaxCandidates,
    int RequestTimeoutSeconds);

public sealed record RecommendationCredentialsUpdate(
    string? OpenAiApiKey,
    bool ClearOpenAiApiKey,
    string? GeminiApiKey,
    bool ClearGeminiApiKey,
    IReadOnlyList<RecommendationNamedCredentialUpdate> AdditionalSecrets);

public sealed record RecommendationNamedCredentialUpdate(
    string Name,
    string? Value,
    bool Clear);

public sealed record RecommendationSettingsUpdate(
    RecommendationProviderSettingsUpdate Provider,
    RecommendationCredentialsUpdate Credentials,
    SelectionConfig Selection);

public interface IRecommendationSettingsStore
{
    RecommendationRuntimeSettings GetCurrent();
    RecommendationSettingsResponse GetResponse();
    Task<RecommendationSettingsResponse> UpdateAsync(RecommendationSettingsUpdate update, CancellationToken ct);
}

public sealed class SqliteRecommendationSettingsStore : IRecommendationSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly object _sync = new();
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly string _connectionString;
    private readonly string? _legacyFilePath;
    private readonly ILogger<SqliteRecommendationSettingsStore> _logger;

    private RecommendationRuntimeSettings _current;

    public SqliteRecommendationSettingsStore(
        string connectionString,
        RecommendationRuntimeSettings defaults,
        ILogger<SqliteRecommendationSettingsStore> logger,
        string? legacyFilePath = null)
    {
        _connectionString = connectionString;
        _legacyFilePath = legacyFilePath;
        _logger = logger;
        _current = LoadInitialSettings(defaults);
    }

    public RecommendationRuntimeSettings GetCurrent()
    {
        lock (_sync)
        {
            return Clone(_current);
        }
    }

    public RecommendationSettingsResponse GetResponse() => ToResponse(GetCurrent());

    public async Task<RecommendationSettingsResponse> UpdateAsync(RecommendationSettingsUpdate update, CancellationToken ct)
    {
        var normalized = Normalize(update);

        await _updateGate.WaitAsync(ct);
        try
        {
            RecommendationRuntimeSettings current;
            lock (_sync)
            {
                current = _current;
            }

            var next = ApplyUpdate(current, normalized);
            await PersistAsync(next, ct);

            lock (_sync)
            {
                _current = next;
            }

            return ToResponse(next);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private RecommendationRuntimeSettings LoadInitialSettings(RecommendationRuntimeSettings defaults)
    {
        var normalizedDefaults = Normalize(defaults);

        try
        {
            using var connection = OpenConnection();
            EnsureSchema(connection);

            if (TryReadPersisted(connection, normalizedDefaults, out var persisted))
            {
                return persisted;
            }

            if (TryLoadLegacySettings(normalizedDefaults, out var migrated))
            {
                Persist(connection, migrated);
                return migrated;
            }

            Persist(connection, normalizedDefaults);
            return Clone(normalizedDefaults);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load recommendation settings from SQLite; using defaults");
            return Clone(normalizedDefaults);
        }
    }

    private async Task PersistAsync(RecommendationRuntimeSettings settings, CancellationToken ct)
    {
        using var connection = OpenConnection();
        EnsureSchema(connection);

        using var command = BuildUpsertCommand(connection, settings);
        await command.ExecuteNonQueryAsync(ct);
    }

    private SqliteConnection OpenConnection()
    {
        EnsureDataDirectory();

        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void EnsureDataDirectory()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) ||
            builder.DataSource == ":memory:")
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS recommendation_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                provider_json TEXT NOT NULL,
                openai_api_key TEXT NULL,
                gemini_api_key TEXT NULL,
                additional_secrets_json TEXT NOT NULL,
                selection_json TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private bool TryReadPersisted(
        SqliteConnection connection,
        RecommendationRuntimeSettings defaults,
        out RecommendationRuntimeSettings persisted)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT provider_json,
                   openai_api_key,
                   gemini_api_key,
                   additional_secrets_json,
                   selection_json
            FROM recommendation_settings
            WHERE id = 1
            LIMIT 1;
            """;

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            persisted = defaults;
            return false;
        }

        var provider = JsonSerializer.Deserialize<OpenAiRecommendationOptions>(reader.GetString(0), SerializerOptions)
                       ?? defaults.Provider;
        var selection = JsonSerializer.Deserialize<SelectionConfig>(reader.GetString(4), SerializerOptions)
                        ?? defaults.Selection;
        var additionalSecrets =
            JsonSerializer.Deserialize<RecommendationNamedCredentialValue[]>(reader.GetString(3), SerializerOptions)
            ?? Array.Empty<RecommendationNamedCredentialValue>();

        var openAiApiKey = reader.IsDBNull(1) ? provider.ApiKey ?? defaults.Credentials.OpenAiApiKey : reader.GetString(1);
        var geminiApiKey = reader.IsDBNull(2) ? defaults.Credentials.GeminiApiKey : reader.GetString(2);

        persisted = Normalize(new RecommendationRuntimeSettings(
            CloneProvider(provider),
            new RecommendationCredentialValues(openAiApiKey, geminiApiKey, additionalSecrets),
            selection));

        return true;
    }

    private bool TryLoadLegacySettings(
        RecommendationRuntimeSettings defaults,
        out RecommendationRuntimeSettings migrated)
    {
        migrated = defaults;
        if (string.IsNullOrWhiteSpace(_legacyFilePath) || !File.Exists(_legacyFilePath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(_legacyFilePath);
            var legacy = JsonSerializer.Deserialize<LegacyPersistedRecommendationSettings>(json, SerializerOptions);
            if (legacy is null)
            {
                return false;
            }

            var credentials = legacy.Credentials is null
                ? defaults.Credentials with
                {
                    OpenAiApiKey = legacy.Provider?.ApiKey ?? defaults.Credentials.OpenAiApiKey
                }
                : legacy.Credentials;

            migrated = Normalize(new RecommendationRuntimeSettings(
                CloneProvider(legacy.Provider ?? defaults.Provider),
                CloneCredentials(credentials),
                legacy.Selection ?? defaults.Selection));

            _logger.LogInformation("Migrated recommendation settings from legacy file {Path}", _legacyFilePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to migrate legacy recommendation settings from {Path}", _legacyFilePath);
            return false;
        }
    }

    private static void Persist(SqliteConnection connection, RecommendationRuntimeSettings settings)
    {
        using var command = BuildUpsertCommand(connection, settings);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand BuildUpsertCommand(SqliteConnection connection, RecommendationRuntimeSettings settings)
    {
        var normalized = Normalize(settings);
        var provider = new OpenAiRecommendationOptions
        {
            ApiKey = null,
            Model = normalized.Provider.Model,
            ReasoningEffort = normalized.Provider.ReasoningEffort,
            MaxCandidates = normalized.Provider.MaxCandidates,
            RequestTimeoutSeconds = normalized.Provider.RequestTimeoutSeconds
        };

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recommendation_settings (
                id,
                provider_json,
                openai_api_key,
                gemini_api_key,
                additional_secrets_json,
                selection_json,
                updated_utc
            )
            VALUES (
                1,
                $provider_json,
                $openai_api_key,
                $gemini_api_key,
                $additional_secrets_json,
                $selection_json,
                $updated_utc
            )
            ON CONFLICT(id) DO UPDATE SET
                provider_json = excluded.provider_json,
                openai_api_key = excluded.openai_api_key,
                gemini_api_key = excluded.gemini_api_key,
                additional_secrets_json = excluded.additional_secrets_json,
                selection_json = excluded.selection_json,
                updated_utc = excluded.updated_utc;
            """;

        command.Parameters.AddWithValue("$provider_json", JsonSerializer.Serialize(provider, SerializerOptions));
        command.Parameters.AddWithValue("$openai_api_key", (object?)normalized.Credentials.OpenAiApiKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$gemini_api_key", (object?)normalized.Credentials.GeminiApiKey ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$additional_secrets_json",
            JsonSerializer.Serialize(normalized.Credentials.AdditionalSecrets, SerializerOptions));
        command.Parameters.AddWithValue("$selection_json", JsonSerializer.Serialize(normalized.Selection, SerializerOptions));
        command.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToString("O"));
        return command;
    }

    private static RecommendationRuntimeSettings ApplyUpdate(
        RecommendationRuntimeSettings current,
        RecommendationSettingsUpdate update)
    {
        var provider = new OpenAiRecommendationOptions
        {
            Model = update.Provider.Model,
            ReasoningEffort = update.Provider.ReasoningEffort,
            MaxCandidates = update.Provider.MaxCandidates,
            RequestTimeoutSeconds = update.Provider.RequestTimeoutSeconds
        };

        var credentials = MergeCredentials(current.Credentials, update);

        return Normalize(new RecommendationRuntimeSettings(provider, credentials, update.Selection));
    }

    private static RecommendationSettingsUpdate Normalize(RecommendationSettingsUpdate update)
    {
        var categories = (update.Selection.TargetCategories ?? Array.Empty<string>())
            .Select(category => category?.Trim())
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();

        return new RecommendationSettingsUpdate(
            new RecommendationProviderSettingsUpdate(
                ApiKey: string.IsNullOrWhiteSpace(update.Provider.ApiKey) ? null : update.Provider.ApiKey.Trim(),
                ClearApiKey: update.Provider.ClearApiKey,
                Model: (update.Provider.Model ?? string.Empty).Trim(),
                ReasoningEffort: (update.Provider.ReasoningEffort ?? string.Empty).Trim().ToLowerInvariant(),
                MaxCandidates: update.Provider.MaxCandidates,
                RequestTimeoutSeconds: update.Provider.RequestTimeoutSeconds),
            NormalizeCredentials(update.Credentials),
            new SelectionConfig(
                TargetCategories: categories,
                MinPrice: update.Selection.MinPrice,
                MaxPrice: update.Selection.MaxPrice,
                MinScore: update.Selection.MinScore,
                TopNPerCategory: update.Selection.TopNPerCategory,
                TargetMarket: (update.Selection.TargetMarket ?? string.Empty).Trim().ToUpperInvariant(),
                MaxShippingDays: update.Selection.MaxShippingDays));
    }

    private static RecommendationRuntimeSettings Normalize(RecommendationRuntimeSettings settings)
    {
        var normalizedCredentials = NormalizeCredentials(settings.Credentials);

        return new RecommendationRuntimeSettings(
            new OpenAiRecommendationOptions
            {
                ApiKey = normalizedCredentials.OpenAiApiKey,
                Model = string.IsNullOrWhiteSpace(settings.Provider.Model) ? "gpt-5" : settings.Provider.Model.Trim(),
                ReasoningEffort = string.IsNullOrWhiteSpace(settings.Provider.ReasoningEffort)
                    ? "low"
                    : settings.Provider.ReasoningEffort.Trim().ToLowerInvariant(),
                MaxCandidates = settings.Provider.MaxCandidates < 1 ? 48 : settings.Provider.MaxCandidates,
                RequestTimeoutSeconds = settings.Provider.RequestTimeoutSeconds < 5
                    ? 90
                    : settings.Provider.RequestTimeoutSeconds
            },
            normalizedCredentials,
            new SelectionConfig(
                TargetCategories: (settings.Selection.TargetCategories ?? Array.Empty<string>())
                    .Select(category => category?.Trim())
                    .Where(category => !string.IsNullOrWhiteSpace(category))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Cast<string>()
                    .ToArray(),
                MinPrice: settings.Selection.MinPrice,
                MaxPrice: settings.Selection.MaxPrice,
                MinScore: settings.Selection.MinScore,
                TopNPerCategory: settings.Selection.TopNPerCategory,
                TargetMarket: (settings.Selection.TargetMarket ?? "IE").Trim().ToUpperInvariant(),
                MaxShippingDays: settings.Selection.MaxShippingDays));
    }

    private static RecommendationRuntimeSettings Clone(RecommendationRuntimeSettings settings)
        => new(CloneProvider(settings.Provider), CloneCredentials(settings.Credentials), settings.Selection);

    private static OpenAiRecommendationOptions CloneProvider(OpenAiRecommendationOptions provider)
        => new()
        {
            ApiKey = provider.ApiKey,
            Model = provider.Model,
            ReasoningEffort = provider.ReasoningEffort,
            MaxCandidates = provider.MaxCandidates,
            RequestTimeoutSeconds = provider.RequestTimeoutSeconds
        };

    private static RecommendationCredentialValues CloneCredentials(RecommendationCredentialValues credentials)
        => new(
            credentials.OpenAiApiKey,
            credentials.GeminiApiKey,
            credentials.AdditionalSecrets
                .Select(secret => new RecommendationNamedCredentialValue(secret.Name, secret.Value))
                .ToArray());

    private static RecommendationSettingsResponse ToResponse(RecommendationRuntimeSettings settings)
        => new(
            new RecommendationProviderSettings(
                HasApiKey: !string.IsNullOrWhiteSpace(settings.Credentials.OpenAiApiKey),
                Model: settings.Provider.Model,
                ReasoningEffort: settings.Provider.ReasoningEffort,
                MaxCandidates: settings.Provider.MaxCandidates,
                RequestTimeoutSeconds: settings.Provider.RequestTimeoutSeconds,
                EffectiveProvider: DetermineEffectiveProvider(settings)),
            new RecommendationCredentialsSettings(
                HasOpenAiApiKey: !string.IsNullOrWhiteSpace(settings.Credentials.OpenAiApiKey),
                HasGeminiApiKey: !string.IsNullOrWhiteSpace(settings.Credentials.GeminiApiKey),
                OpenAiApiKeyPreview: PreviewSecret(settings.Credentials.OpenAiApiKey),
                GeminiApiKeyPreview: PreviewSecret(settings.Credentials.GeminiApiKey),
                AdditionalSecrets: settings.Credentials.AdditionalSecrets
                    .Select(secret => new RecommendationNamedCredentialStatus(
                        secret.Name,
                        !string.IsNullOrWhiteSpace(secret.Value),
                        PreviewSecret(secret.Value)))
                    .ToArray()),
            settings.Selection);

    private static string DetermineEffectiveProvider(RecommendationRuntimeSettings settings)
    {
        var model = settings.Provider.Model?.Trim();
        var hasOpenAiKey = !string.IsNullOrWhiteSpace(settings.Credentials.OpenAiApiKey);
        var hasGeminiKey = !string.IsNullOrWhiteSpace(settings.Credentials.GeminiApiKey);
        var prefersGemini = !string.IsNullOrWhiteSpace(model) &&
                            model.StartsWith("gemini", StringComparison.OrdinalIgnoreCase);

        if (hasGeminiKey && (!hasOpenAiKey || prefersGemini))
        {
            return "Gemini";
        }

        if (hasOpenAiKey)
        {
            return "OpenAI";
        }

        return "None";
    }

    private static string? PreviewSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 4
            ? new string('*', trimmed.Length)
            : $"****{trimmed[^4..]}";
    }

    private static RecommendationCredentialValues MergeCredentials(
        RecommendationCredentialValues current,
        RecommendationSettingsUpdate update)
    {
        var openAiApiKey = ResolveCredentialValue(
            update.Credentials.ClearOpenAiApiKey || update.Provider.ClearApiKey,
            update.Credentials.OpenAiApiKey ?? update.Provider.ApiKey,
            current.OpenAiApiKey);

        var geminiApiKey = ResolveCredentialValue(
            update.Credentials.ClearGeminiApiKey,
            update.Credentials.GeminiApiKey,
            current.GeminiApiKey);

        var currentAdditional = current.AdditionalSecrets
            .ToDictionary(secret => secret.Name, secret => secret.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var secret in update.Credentials.AdditionalSecrets)
        {
            if (secret.Clear)
            {
                currentAdditional.Remove(secret.Name);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(secret.Value))
            {
                currentAdditional[secret.Name] = secret.Value.Trim();
            }
        }

        return new RecommendationCredentialValues(
            openAiApiKey,
            geminiApiKey,
            currentAdditional
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new RecommendationNamedCredentialValue(entry.Key, entry.Value))
                .ToArray());
    }

    private static string? ResolveCredentialValue(bool clear, string? incomingValue, string? currentValue)
    {
        if (clear)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(incomingValue)
            ? currentValue
            : incomingValue.Trim();
    }

    private static RecommendationCredentialsUpdate NormalizeCredentials(RecommendationCredentialsUpdate credentials)
    {
        var extras = (credentials.AdditionalSecrets ?? Array.Empty<RecommendationNamedCredentialUpdate>())
            .Where(secret => !string.IsNullOrWhiteSpace(secret.Name))
            .GroupBy(secret => secret.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latest = group.Last();
                return new RecommendationNamedCredentialUpdate(
                    group.Key,
                    string.IsNullOrWhiteSpace(latest.Value) ? null : latest.Value.Trim(),
                    latest.Clear);
            })
            .ToArray();

        return new RecommendationCredentialsUpdate(
            OpenAiApiKey: string.IsNullOrWhiteSpace(credentials.OpenAiApiKey) ? null : credentials.OpenAiApiKey.Trim(),
            ClearOpenAiApiKey: credentials.ClearOpenAiApiKey,
            GeminiApiKey: string.IsNullOrWhiteSpace(credentials.GeminiApiKey) ? null : credentials.GeminiApiKey.Trim(),
            ClearGeminiApiKey: credentials.ClearGeminiApiKey,
            AdditionalSecrets: extras);
    }

    private static RecommendationCredentialValues NormalizeCredentials(RecommendationCredentialValues credentials)
    {
        var extras = (credentials.AdditionalSecrets ?? Array.Empty<RecommendationNamedCredentialValue>())
            .Where(secret => !string.IsNullOrWhiteSpace(secret.Name))
            .GroupBy(secret => secret.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latest = group.Last();
                return new RecommendationNamedCredentialValue(group.Key, latest.Value.Trim());
            })
            .Where(secret => !string.IsNullOrWhiteSpace(secret.Value))
            .OrderBy(secret => secret.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new RecommendationCredentialValues(
            OpenAiApiKey: string.IsNullOrWhiteSpace(credentials.OpenAiApiKey) ? null : credentials.OpenAiApiKey.Trim(),
            GeminiApiKey: string.IsNullOrWhiteSpace(credentials.GeminiApiKey) ? null : credentials.GeminiApiKey.Trim(),
            AdditionalSecrets: extras);
    }

    private sealed record LegacyPersistedRecommendationSettings(
        OpenAiRecommendationOptions? Provider,
        RecommendationCredentialValues? Credentials,
        SelectionConfig? Selection);
}
