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
        "BA2State, Compression.BSA" or "BA2State" => new Ba2ArchiveState(
            HasNameTable: Bool(element, "HasNameTable"),
            Type: EntryType(element),
            HeaderMagic: String(element, "HeaderMagic"),
            Version: UInt32(element, "Version"),
            // Lists compiled before Starfield have no such fields.
            Unknown1: OptionalUInt32(element, "Unknown1"),
            Unknown2: OptionalUInt32(element, "Unknown2"),
            Compression: OptionalUInt32(element, "Compression")),
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
        "BA2File" or "BA2FileEntryState, Compression.BSA" => new Ba2FileState(
            Index: Int32(element, "Index"),
            Path: Path(element),
            Extension: String(element, "Extension"),
            Compressed: Bool(element, "Compressed"),
            Align: UInt32(element, "Align"),
            Flags: UInt32(element, "Flags"),
            DirHash: UInt32(element, "DirHash"),
            NameHash: UInt32(element, "NameHash")),
        "BA2DX10Entry" or "BA2DX10Entry, Compression.BSA" => new Ba2Dx10FileState(
            Index: Int32(element, "Index"),
            Path: Path(element),
            Chunks: Chunks(element),
            PixelFormat: Byte(element, "PixelFormat"),
            NumMips: Byte(element, "NumMips"),
            Width: UInt16(element, "Width"),
            Height: UInt16(element, "Height"),
            ChunkHdrLen: UInt16(element, "ChunkHdrLen"),
            Unk8: Byte(element, "Unk8"),
            DirHash: UInt32(element, "DirHash"),
            Extension: String(element, "Extension"),
            NameHash: UInt32(element, "NameHash"),
            IsCubeMap: Byte(element, "IsCubeMap"),
            TileMode: Byte(element, "TileMode")),
        var other => throw new BethesdaArchiveException($"Unknown file state type '{other}'"),
    };

    // Wabbajack writes the enum as a number; a hand-edited list may spell the name.
    private static Ba2EntryType EntryType(JsonElement element)
    {
        var value = Field(element, "Type");
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && Enum.IsDefined((Ba2EntryType)number))
            return (Ba2EntryType)number;
        if (value.ValueKind == JsonValueKind.String && Enum.TryParse<Ba2EntryType>(value.GetString(), ignoreCase: true, out var named))
            return named;
        throw WrongType("Type", "a BA2 content type (0 to 2, or GNRL, DX10, GNMF)", value);
    }

    private static Ba2Chunk[] Chunks(JsonElement element)
    {
        var value = Field(element, "Chunks");
        if (value.ValueKind != JsonValueKind.Array) throw WrongType("Chunks", "an array", value);
        return value.EnumerateArray().Select(chunk => new Ba2Chunk(
            Compressed: Bool(chunk, "Compressed"),
            Align: UInt32(chunk, "Align"),
            EndMip: UInt16(chunk, "EndMip"),
            StartMip: UInt16(chunk, "StartMip"),
            FullSz: UInt32(chunk, "FullSz"))).ToArray();
    }

    private static string TypeName(JsonElement element) => String(element, "$type");

    private static RelativePath Path(JsonElement element) => RelativePath.FromUnsanitizedInput(String(element, "Path"));

    private static bool HasField(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

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

    private static uint OptionalUInt32(JsonElement element, string name) =>
        HasField(element, name) ? UInt32(element, name) : 0;

    private static ushort UInt16(JsonElement element, string name)
    {
        var value = Field(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetUInt16(out var result) ? result : throw WrongType(name, "an unsigned 16 bit number", value);
    }

    private static byte Byte(JsonElement element, string name)
    {
        var value = Field(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetByte(out var result) ? result : throw WrongType(name, "a byte", value);
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
