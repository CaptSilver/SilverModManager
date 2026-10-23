using System.Text;
using K4os.Compression.LZ4;

namespace NexusMods.Games.Larian.Tests.BaldursGate3;

/// <summary>
/// Builds small version 18 Larian packages in memory: stored (uncompressed) entries and an LZ4 file table,
/// which is all the pak reader needs. Keeps the tests off the real game's multi-gigabyte paks.
/// </summary>
public static class SyntheticPak
{
    private const int NameSize = 256;
    private const int HeaderSize = 4 + 4 + 8 + 4 + 1 + 1 + 16 + 2;

    public static byte[] Build(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // The header is patched once the file table position is known.
        writer.Write(new byte[HeaderSize]);

        var table = new MemoryStream();
        using var tableWriter = new BinaryWriter(table);
        foreach (var (name, content) in entries)
        {
            var data = Encoding.UTF8.GetBytes(content);
            var offset = ms.Position;
            writer.Write(data);

            var nameBytes = new byte[NameSize];
            Encoding.UTF8.GetBytes(name, nameBytes);
            tableWriter.Write(nameBytes);
            tableWriter.Write((uint)(offset & 0xFFFFFFFF));
            tableWriter.Write((ushort)(offset >> 32));
            tableWriter.Write((byte)0); // archive part
            tableWriter.Write((byte)0); // stored, no compression
            tableWriter.Write((uint)data.Length);
            tableWriter.Write((uint)data.Length);
        }

        var raw = table.ToArray();
        var compressed = new byte[LZ4Codec.MaximumOutputSize(raw.Length)];
        var compressedLength = LZ4Codec.Encode(raw, 0, raw.Length, compressed, 0, compressed.Length);

        var tableOffset = ms.Position;
        writer.Write(entries.Length);
        writer.Write(compressedLength);
        writer.Write(compressed, 0, compressedLength);
        var tableSize = ms.Position - tableOffset;

        ms.Position = 0;
        writer.Write(Encoding.ASCII.GetBytes("LSPK"));
        writer.Write(18u);
        writer.Write((ulong)tableOffset);
        writer.Write((uint)tableSize);
        writer.Write((byte)0); // flags
        writer.Write((byte)0); // priority
        writer.Write(new byte[16]); // md5
        writer.Write((ushort)1); // parts
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
    {
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
