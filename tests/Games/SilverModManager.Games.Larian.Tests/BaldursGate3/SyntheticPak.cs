using System.Text;
using K4os.Compression.LZ4;

namespace SilverModManager.Games.Larian.Tests.BaldursGate3;

/// <summary>
/// Builds small version 18 Larian packages in memory: stored (uncompressed) entries and an LZ4 file table,
/// which is all the pak reader needs. Keeps the tests off the real game's multi-gigabyte paks.
/// </summary>
public static class SyntheticPak
{
    private const int NameSize = 256;
    private const int HeaderSize = 4 + 4 + 8 + 4 + 1 + 1 + 16 + 2;

    // WithArchivePart and WithSolidFlag rebuild from the recipe, since the file table of most versions is compressed.
    private sealed record Recipe(int Version, (string Name, string Content)[] Entries, IReadOnlyDictionary<string, uint> Parts, bool Solid);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], Recipe> Recipes = new();

    public static byte[] Build(params (string Name, string Content)[] entries) => Build(18, entries);

    /// <summary>Builds a package of the given LSPK version (7, 9, 10, 13, 15, 16 or 18).</summary>
    public static byte[] Build(int version, params (string Name, string Content)[] entries)
        => Build(new Recipe(version, entries, new Dictionary<string, uint>(), false));

    /// <summary>The same pak with the named entry recorded as living in part <paramref name="part"/>.</summary>
    public static byte[] WithArchivePart(byte[] pak, string entryName, uint part)
    {
        var recipe = Recipes.TryGetValue(pak, out var r) ? r : throw new ArgumentException("Not a pak built by SyntheticPak", nameof(pak));
        var parts = new Dictionary<string, uint>(recipe.Parts) { [entryName] = part };
        return Build(recipe with { Parts = parts });
    }

    /// <summary>The same pak with the header's Solid flag set (versions 10 and later have a flags byte).</summary>
    public static byte[] WithSolidFlag(byte[] pak)
    {
        var recipe = Recipes.TryGetValue(pak, out var r) ? r : throw new ArgumentException("Not a pak built by SyntheticPak", nameof(pak));
        return Build(recipe with { Solid = true });
    }

    private static byte[] Build(Recipe recipe)
    {
        var bytes = recipe.Version switch
        {
            7 or 9 => BuildBareHeader(recipe),
            10 => BuildV10(recipe),
            13 => BuildV13(recipe),
            15 or 16 or 18 => BuildModern(recipe),
            _ => throw new ArgumentOutOfRangeException(nameof(recipe), $"No builder for LSPK v{recipe.Version}"),
        };
        Recipes.Add(bytes, recipe);
        return bytes;
    }

    private const int LegacyEntrySize = NameSize + 16;   // FileEntry7
    private const int Entry10Size = NameSize + 24;       // FileEntry10

    private static byte[] NameBytes(string name)
    {
        var nameBytes = new byte[NameSize];
        Encoding.UTF8.GetBytes(name, nameBytes);
        return nameBytes;
    }

    private static uint PartOf(Recipe recipe, string name) => recipe.Parts.GetValueOrDefault(name);

    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
            z.Write(data);
        return ms.ToArray();
    }

    private static byte[] Lz4(byte[] data)
    {
        var buffer = new byte[LZ4Codec.MaximumOutputSize(data.Length)];
        var length = LZ4Codec.Encode(data, 0, data.Length, buffer, 0, buffer.Length);
        return buffer.AsSpan(0, length).ToArray();
    }

    /// <summary>v7 and v9: no signature, first Int32 is the version, an uncompressed table right after the header, zlib bodies.</summary>
    private static byte[] BuildBareHeader(Recipe recipe)
    {
        const int headerSize = 4 + 4 + 4 + 4 + 1 + 4;
        var bodies = recipe.Entries.Select(e => Encoding.UTF8.GetBytes(e.Content)).ToArray();
        var stored = bodies.Select(b => b.Length > 0 ? Zlib(b) : b).ToArray();
        var tableSize = recipe.Entries.Length * LegacyEntrySize;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((uint)recipe.Version);
        w.Write((uint)(headerSize + tableSize)); // data offset
        w.Write(1u);                              // parts
        w.Write((uint)tableSize);
        w.Write((byte)1);                         // little endian
        w.Write((uint)recipe.Entries.Length);

        uint offset = 0;
        for (var i = 0; i < recipe.Entries.Length; i++)
        {
            w.Write(NameBytes(recipe.Entries[i].Name));
            w.Write(offset);
            w.Write((uint)stored[i].Length);
            w.Write((uint)bodies[i].Length);
            w.Write(PartOf(recipe, recipe.Entries[i].Name));
            offset += (uint)stored[i].Length;
        }
        foreach (var body in stored) w.Write(body);
        return ms.ToArray();
    }

    /// <summary>v10: "LSPK" and the version, then an uncompressed table and zlib bodies.</summary>
    private static byte[] BuildV10(Recipe recipe)
    {
        const int headerSize = 4 + 4 + 4 + 2 + 1 + 1 + 4;
        var bodies = recipe.Entries.Select(e => Encoding.UTF8.GetBytes(e.Content)).ToArray();
        var stored = bodies.Select(b => b.Length > 0 ? Zlib(b) : b).ToArray();
        var tableSize = recipe.Entries.Length * Entry10Size;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("LSPK"));
        w.Write(10u);
        w.Write((uint)(4 + headerSize + tableSize)); // data offset: signature, header, table
        w.Write((uint)tableSize);
        w.Write((ushort)1);
        w.Write((byte)(recipe.Solid ? 0x04 : 0));
        w.Write((byte)0);
        w.Write((uint)recipe.Entries.Length);

        uint offset = 0;
        for (var i = 0; i < recipe.Entries.Length; i++)
        {
            w.Write(NameBytes(recipe.Entries[i].Name));
            w.Write(offset);
            w.Write((uint)stored[i].Length);
            w.Write((uint)bodies[i].Length);
            w.Write(PartOf(recipe, recipe.Entries[i].Name));
            w.Write(bodies[i].Length > 0 ? 1u : 0u); // zlib or stored
            w.Write(0u);                              // crc
            offset += (uint)stored[i].Length;
        }
        foreach (var body in stored) w.Write(body);
        return ms.ToArray();
    }

    /// <summary>v13: stored bodies first, an LZ4 table, then the header and a size and signature trailer.</summary>
    private static byte[] BuildV13(Recipe recipe)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        var table = new MemoryStream();
        using var tableWriter = new BinaryWriter(table);
        foreach (var (name, content) in recipe.Entries)
        {
            var data = Encoding.UTF8.GetBytes(content);
            // Stored as is, so a body can start with "LSPK" like a nested package would.
            var offset = (uint)ms.Position;
            w.Write(data);

            tableWriter.Write(NameBytes(name));
            tableWriter.Write(offset);
            tableWriter.Write((uint)data.Length);
            tableWriter.Write((uint)data.Length);
            tableWriter.Write(PartOf(recipe, name));
            tableWriter.Write(0u); // stored
            tableWriter.Write(0u);                         // crc
        }

        var compressedTable = Lz4(table.ToArray());
        var tableOffset = (uint)ms.Position;
        w.Write(recipe.Entries.Length);
        w.Write(compressedTable);

        const int headerSize = 4 + 4 + 4 + 2 + 1 + 1 + 16;
        w.Write(13u);
        w.Write(tableOffset);
        w.Write((uint)(4 + compressedTable.Length));
        w.Write((ushort)1);
        w.Write((byte)(recipe.Solid ? 0x04 : 0));
        w.Write((byte)0);
        w.Write(new byte[16]);
        w.Write(headerSize + 8);
        w.Write(Encoding.ASCII.GetBytes("LSPK"));
        return ms.ToArray();
    }

    /// <summary>v15, v16 and v18: LZ4 table behind the "LSPK" header, stored bodies.</summary>
    private static byte[] BuildModern(Recipe recipe)
    {
        var headerSize = recipe.Version == 15 ? HeaderSize - 2 : HeaderSize;

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // The header is patched once the file table position is known.
        writer.Write(new byte[headerSize]);

        var table = new MemoryStream();
        using var tableWriter = new BinaryWriter(table);
        foreach (var (name, content) in recipe.Entries)
        {
            var data = Encoding.UTF8.GetBytes(content);
            var offset = ms.Position;
            writer.Write(data);

            var part = PartOf(recipe, name);
            tableWriter.Write(NameBytes(name));
            if (recipe.Version == 18)
            {
                tableWriter.Write((uint)(offset & 0xFFFFFFFF));
                tableWriter.Write((ushort)(offset >> 32));
                tableWriter.Write((byte)part);
                tableWriter.Write((byte)0); // stored, no compression
                tableWriter.Write((uint)data.Length);
                tableWriter.Write((uint)data.Length);
            }
            else
            {
                tableWriter.Write((ulong)offset);
                tableWriter.Write((ulong)data.Length);
                tableWriter.Write((ulong)data.Length);
                tableWriter.Write(part);
                tableWriter.Write(0u); // flags: stored
                tableWriter.Write(0u); // crc
                tableWriter.Write(0u);
            }
        }

        var raw = table.ToArray();
        var compressed = Lz4(raw);

        var tableOffset = ms.Position;
        writer.Write(recipe.Entries.Length);
        writer.Write(compressed.Length);
        writer.Write(compressed);
        var tableSize = ms.Position - tableOffset;

        ms.Position = 0;
        writer.Write(Encoding.ASCII.GetBytes("LSPK"));
        writer.Write((uint)recipe.Version);
        writer.Write((ulong)tableOffset);
        writer.Write((uint)tableSize);
        writer.Write((byte)(recipe.Solid ? 0x04 : 0)); // flags
        writer.Write((byte)0); // priority
        writer.Write(new byte[16]); // md5
        if (recipe.Version != 15) writer.Write((ushort)1); // parts
        return ms.ToArray();
    }

    /// <summary>A pak holding the single module <paramref name="folder"/> at Mods/&lt;folder&gt;/meta.lsx.</summary>
    public static byte[] ForModule(string folder, string name, string uuid, ulong version, params Dependency[] dependencies)
        => Build(($"Mods/{folder}/meta.lsx", MetaLsx(folder, name, uuid, version, dependencies)));

    /// <summary>Like <see cref="ForModule(string,string,string,ulong,Dependency[])"/> for a module published on mod.io.</summary>
    public static byte[] ForPublishedModule(string folder, string name, string uuid, ulong version, string publishHandle)
        => Build(($"Mods/{folder}/meta.lsx", MetaLsx(folder, name, uuid, version, publishHandle)));

    public static string MetaLsx(string folder, string name, string uuid, ulong version, params Dependency[] dependencies)
        => MetaLsx(folder, name, uuid, version, "0", dependencies);

    public static string MetaLsx(string folder, string name, string uuid, ulong version, string publishHandle, params Dependency[] dependencies)
        => MetaLsx(folder, name, uuid, version, publishHandle, type: null, dependencies);

    /// <param name="type">The module's <c>Type</c> attribute ("Adventure", "Add-on"); omitted when null.</param>
    public static string MetaLsx(string folder, string name, string uuid, ulong version, string publishHandle, string? type, params Dependency[] dependencies)
    {
        var typeAttribute = type is null ? "" : $"""<attribute id="Type" type="FixedString" value="{type}" />""";
        var deps = string.Concat(dependencies.Select(d => $"""
                    <node id="ModuleShortDesc">
                        <attribute id="Folder" type="LSString" value="{d.Folder}" />
                        <attribute id="MD5" type="LSString" value="" />
                        <attribute id="Name" type="LSString" value="{d.Name}" />
                        <attribute id="PublishHandle" type="uint64" value="0" />
                        <attribute id="UUID" type="guid" value="{d.Uuid}" />
                        <attribute id="Version64" type="int64" value="{d.Version}" />
                    </node>
            """));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <save>
                <version major="4" minor="0" revision="9" build="331" />
                <region id="Config">
                    <node id="root">
                        <children>
                            <node id="Dependencies">
                                <children>
            {deps}
                                </children>
                            </node>
                            <node id="ModuleInfo">
                                <attribute id="Folder" type="LSString" value="{folder}" />
                                <attribute id="MD5" type="LSString" value="" />
                                <attribute id="Name" type="LSString" value="{name}" />
                                <attribute id="PublishHandle" type="uint64" value="{publishHandle}" />
                                <attribute id="UUID" type="FixedString" value="{uuid}" />
                                {typeAttribute}
                                <attribute id="Version64" type="int64" value="{version}" />
                            </node>
                        </children>
                    </node>
                </region>
            </save>
            """;
    }

    /// <summary>The 64-bit module version Larian stores in a meta.lsx.</summary>
    public static ulong Version(ulong major, ulong minor = 0, ulong patch = 0, ulong build = 0)
        => (major << 55) | (minor << 47) | (patch << 31) | build;

    public readonly record struct Dependency(string Name, string Uuid, ulong Version)
    {
        public string Folder => Name;
    }
}
