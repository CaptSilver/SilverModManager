using System.Text;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Conditions;

/// <summary>Bytes of files that look enough like a PE image with a version resource for the version readers.</summary>
internal static class FakePe
{
    private static byte[] Node(string key, byte[] value, int valueLength, short type, params byte[][] children)
    {
        var bytes = new List<byte>([0, 0]);
        bytes.AddRange(BitConverter.GetBytes((ushort)valueLength));
        bytes.AddRange(BitConverter.GetBytes(type));
        bytes.AddRange(Encoding.Unicode.GetBytes(key + "\0"));
        while (bytes.Count % 4 != 0) bytes.Add(0);
        bytes.AddRange(value);
        foreach (var child in children)
        {
            while (bytes.Count % 4 != 0) bytes.Add(0);
            bytes.AddRange(child);
        }

        var all = bytes.ToArray();
        BitConverter.TryWriteBytes(all.AsSpan(0), (ushort)all.Length);
        return all;
    }

    private static byte[] FixedFileInfo(Version fileVersion)
    {
        var bytes = new byte[0x34];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), 0xFEEF04BDu);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), 0x00010000u);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), (uint)(fileVersion.Major << 16 | fileVersion.Minor));
        BitConverter.TryWriteBytes(bytes.AsSpan(12), (uint)(fileVersion.Build << 16 | fileVersion.Revision));
        return bytes;
    }

    /// <summary>A file with the given file version and, when <paramref name="productVersion"/> is set, a ProductVersion string.</summary>
    public static byte[] WithVersion(Version fileVersion, string? productVersion = null)
    {
        var strings = productVersion is null
            ? []
            : new[]
            {
                Node("StringFileInfo", [], 0, 1,
                    Node("040904B0", [], 0, 1,
                        Node("ProductVersion", Encoding.Unicode.GetBytes(productVersion + "\0"), productVersion.Length + 1, 1))),
            };
        var resource = Node("VS_VERSION_INFO", FixedFileInfo(fileVersion), 0x34, 0, strings);

        var bytes = new byte[0x200 + resource.Length];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.TryWriteBytes(bytes.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(bytes.AsSpan(0x80));
        BitConverter.TryWriteBytes(bytes.AsSpan(0x80 + 4 + 16), (ushort)0xE0);
        BitConverter.TryWriteBytes(bytes.AsSpan(0x80 + 4 + 20), (ushort)0x10b);
        resource.CopyTo(bytes, 0x200);
        return bytes;
    }
}
