using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoCommerce.Shared.Events;

public sealed class DomainEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Type { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public JsonElement Payload { get; init; }

    public static DomainEvent Create<T>(string type, string source, T payload)
    {
        var json = JsonSerializer.SerializeToElement(payload, SerializerOptions);
        return new DomainEvent { Type = type, Source = source, Payload = json };
    }

    public T? GetPayload<T>() => Payload.ValueKind == JsonValueKind.Undefined
        ? default
        : JsonSerializer.Deserialize<T>(Payload.GetRawText(), SerializerOptions);

    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };
}
