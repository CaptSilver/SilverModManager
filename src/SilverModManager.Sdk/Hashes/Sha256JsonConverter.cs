using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SilverModManager.Sdk.Hashes;

internal class Sha256JsonConverter : JsonConverter<Sha256Value>
{
    public override Sha256Value Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var hex = reader.GetString();
        if (hex is null) throw new JsonException();

        return Sha256Value.FromHex(hex);
    }

    public override void Write(Utf8JsonWriter writer, Sha256Value value, JsonSerializerOptions options)
    {
        Span<char> hex = stackalloc char[Sha256Value.HexStringSize];

        var numWritten = value.ToHex(hex);
        Debug.Assert(numWritten == hex.Length, "entire span should be written to");

        writer.WriteStringValue(hex);
    }
}
