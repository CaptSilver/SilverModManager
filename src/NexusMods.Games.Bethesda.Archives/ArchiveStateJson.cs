using System.Text.Json;
using NexusMods.Paths;

namespace NexusMods.Games.Bethesda.Archives;

/// <summary>
/// Reads the archive and file states Wabbajack stores in a modlist's CreateBSA directive. It accepts both
/// the long ("BSAState, Compression.BSA") and short type names, and field names in any case.
/// </summary>
public static class ArchiveStateJson
{
    public static ArchiveState ParseArchiveState(JsonElement element) => TypeName(element) switch
    {
        "BSAState, Compression.BSA" or "BSAState" => new Tes5ArchiveState(
            Magic: String(element, "Magic"),
            Version: UInt32(element, "Version"),
            ArchiveFlags: UInt32(element, "ArchiveFlags"),
            FileFlags: UInt32(element, "FileFlags")),
        "TES3State" or "TES3State, Compression.BSA" => new Tes3ArchiveState(
            FileCount: UInt32(element, "FileCount"),
            DataOffset: Int64(element, "DataOffset"),
            HashOffset: UInt32(element, "HashOffset"),
            VersionNumber: UInt32(element, "VersionNumber")),
        var other => throw new BethesdaArchiveException($"Unknown archive state type '{other}'"),
    };

    public static FileState ParseFileState(JsonElement element) => TypeName(element) switch
    {
        "BSAFileState, Compression.BSA" or "BSAFile" => new Tes5FileState(
            Index: Int32(element, "Index"),
            Path: Path(element),
            FlipCompression: Bool(element, "FlipCompression")),
        "TES3File" => new Tes3FileState(
            Index: Int32(element, "Index"),
            Path: Path(element),
            Offset: UInt32(element, "Offset"),
            NameOffset: UInt32(element, "NameOffset"),
            Hash1: UInt32(element, "Hash1"),
            Hash2: UInt32(element, "Hash2"),
            Size: UInt32(element, "Size")),
        var other => throw new BethesdaArchiveException($"Unknown file state type '{other}'"),
    };

    private static string TypeName(JsonElement element) => String(element, "$type");

    private static RelativePath Path(JsonElement element) => RelativePath.FromUnsanitizedInput(String(element, "Path"));

    private static JsonElement Field(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new BethesdaArchiveException($"Expected an object holding '{name}', found {element.ValueKind}");

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        }

        throw new BethesdaArchiveException($"Archive state is missing the field '{name}'");
    }

    private static string String(JsonElement element, string name)
    {
        var value = Field(element, name);
        return value.ValueKind == JsonValueKind.String && value.GetString() is { } text ? text : throw WrongType(name, "a string", value);
    }

    private static uint UInt32(JsonElement element, string name)
    {
        var value = Field(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var result) ? result : throw WrongType(name, "an unsigned 32 bit number", value);
    }

    private static int Int32(JsonElement element, string name)
    {
        var value = Field(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : throw WrongType(name, "a 32 bit number", value);
    }

    private static long Int64(JsonElement element, string name)
    {
        var value = Field(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result) ? result : throw WrongType(name, "a 64 bit number", value);
    }

    private static bool Bool(JsonElement element, string name)
    {
        var value = Field(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw WrongType(name, "a boolean", value),
        };
    }

    private static BethesdaArchiveException WrongType(string name, string expected, JsonElement actual) =>
        new($"Archive state field '{name}' should be {expected}, found {actual.ValueKind}");
}
