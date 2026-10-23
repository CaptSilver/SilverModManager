using System.Text.Json;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;

namespace SilverModManager.Wabbajack.Format;

/// <summary>
/// Reads fields of a JSON object the way Wabbajack's serializer does: names in any case, nulls as absent, and an
/// error naming <paramref name="context"/> and the field when a required one is missing or malformed.
/// </summary>
internal readonly struct JsonObject
{
    private readonly JsonElement _element;
    private readonly string _context;

    public JsonObject(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new UnsupportedModlistException($"{context} is not a JSON object");
        _element = element;
        _context = context;
    }

    public JsonElement? Find(string name)
    {
        foreach (var property in _element.EnumerateObject())
        {
            if (!property.NameEquals(name) && !string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            return property.Value.ValueKind == JsonValueKind.Null ? null : property.Value;
        }
        return null;
    }

    public JsonElement Required(string name) => Find(name) ?? throw Missing(name);

    public string? OptionalString(string name)
    {
        var value = Find(name);
        if (value is null) return null;
        return value.Value.ValueKind == JsonValueKind.String
            ? value.Value.GetString()
            : throw Malformed(name, "is not a string");
    }

    public string String(string name) => OptionalString(name) ?? throw Missing(name);

    public long Long(string name)
    {
        var value = Required(name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : throw Malformed(name, "is not an integer");
    }

    public int Int(string name) => checked((int)Long(name));

    public bool Bool(string name)
    {
        var value = Find(name);
        return value?.ValueKind switch
        {
            null => false,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Malformed(name, "is not a boolean"),
        };
    }

    public Hash Hash(string name) => ParseHash(name, String(name));

    public RelativePath Path(string name) => ModlistPath.Sanitize(String(name));

    public JsonObject Object(string name) => new(Required(name), $"{_context} field '{name}'");

    public IEnumerable<JsonElement> Array(string name)
    {
        var value = Required(name);
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : throw Malformed(name, "is not an array");
    }

    public IEnumerable<JsonElement>? OptionalArray(string name)
    {
        var value = Find(name);
        if (value is null) return null;
        return value.Value.ValueKind == JsonValueKind.Array ? value.Value.EnumerateArray() : throw Malformed(name, "is not an array");
    }

    public Hash ParseHash(string name, string base64)
    {
        try
        {
            return WabbajackHash.Parse(base64);
        }
        catch (FormatException)
        {
            throw Malformed(name, $"is not a hash ('{base64}')");
        }
    }

    public UnsupportedModlistException Missing(string name) => new($"{_context} has no '{name}'");

    public UnsupportedModlistException Malformed(string name, string problem) => new($"{_context} field '{name}' {problem}");
}
