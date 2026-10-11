using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace KiriChess.Server.Data;

/// <summary>Serializes a property to a JSON string for storage in a Postgres <c>jsonb</c> column.</summary>
public sealed class JsonValueConverter<T> : ValueConverter<T, string> where T : class
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public JsonValueConverter()
        : base(
            v => JsonSerializer.Serialize(v, Options),
            v => JsonSerializer.Deserialize<T>(v, Options)!)
    { }
}

/// <summary>Deep-equality comparer for JSON-serialized properties (snapshot via re-serialization).</summary>
public sealed class JsonValueComparer<T> : ValueComparer<T> where T : class
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public JsonValueComparer()
        : base(
            (a, b) => JsonSerializer.Serialize(a, Options) == JsonSerializer.Serialize(b, Options),
            v => v == null ? 0 : JsonSerializer.Serialize(v, Options).GetHashCode(),
            v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Options), Options)!)
    { }
}
