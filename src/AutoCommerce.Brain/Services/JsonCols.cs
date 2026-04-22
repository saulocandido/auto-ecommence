using System.Text.Json;
using AutoCommerce.Shared.Events;

namespace AutoCommerce.Brain.Services;

internal static class JsonCols
{
    private static readonly JsonSerializerOptions Opts = DomainEvent.SerializerOptions;

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Opts);

    public static T Deserialize<T>(string json, T fallback)
    {
        if (string.IsNullOrWhiteSpace(json)) return fallback;
        try { return JsonSerializer.Deserialize<T>(json, Opts) ?? fallback; }
        catch { return fallback; }
    }
}
