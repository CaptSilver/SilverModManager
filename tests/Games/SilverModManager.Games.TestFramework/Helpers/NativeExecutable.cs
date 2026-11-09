namespace SilverModManager.Games.TestFramework.Helpers;

/// <summary>
/// The bytes of a native executable's stand-in: padding around a VS_FIXEDFILEINFO, which is all the version is read
/// from. There is no managed metadata, so the reader in NexusMods.Paths has nothing to take a version from on Linux.
/// </summary>
public static class NativeExecutable
{
    public static byte[] WithFileVersion(Version version) => WithFileVersion((uint)(version.Major << 16 | version.Minor), (uint)(version.Build << 16 | version.Revision));

    public static byte[] WithFileVersion(uint fileVersionMs, uint fileVersionLs)
    {
        var bytes = new byte[400];
        Array.Fill(bytes, (byte)0xAB);
        BitConverter.TryWriteBytes(bytes.AsSpan(100), 0xFEEF04BDu);
        BitConverter.TryWriteBytes(bytes.AsSpan(104), 0x00010000u);
        BitConverter.TryWriteBytes(bytes.AsSpan(108), fileVersionMs);
        BitConverter.TryWriteBytes(bytes.AsSpan(112), fileVersionLs);
        return bytes;
    }

    /// <summary>
    /// Like <see cref="WithFileVersion(uint, uint)"/>, but the struct sits in a whole <c>VS_VERSIONINFO</c> resource
    /// whose string table carries <paramref name="productVersion"/> as its <c>ProductVersion</c>.
    /// </summary>
    public static byte[] WithVersionResource(uint fileVersionMs, uint fileVersionLs, string productVersion)
    {
        var fixedInfo = new byte[0x34];
        BitConverter.TryWriteBytes(fixedInfo.AsSpan(0), 0xFEEF04BDu);
        BitConverter.TryWriteBytes(fixedInfo.AsSpan(4), 0x00010000u);
        BitConverter.TryWriteBytes(fixedInfo.AsSpan(8), fileVersionMs);
        BitConverter.TryWriteBytes(fixedInfo.AsSpan(12), fileVersionLs);

        var text = System.Text.Encoding.Unicode.GetBytes(productVersion + "\0");
        var table = Node("StringFileInfo", [], 0, 1, Node("040904B0", [], 0, 1, Node("ProductVersion", text, productVersion.Length + 1, 1)));
        var resource = Node("VS_VERSION_INFO", fixedInfo, fixedInfo.Length, 0, table);

        var bytes = new byte[100 + resource.Length + 100];
        Array.Fill(bytes, (byte)0xAB);
        resource.CopyTo(bytes, 100);
        return bytes;
    }

    /// <summary>One node of a version resource: length, value length, type, UTF-16 key, padding, value, then the children.</summary>
    private static byte[] Node(string key, byte[] value, int valueLength, short type, params byte[][] children)
    {
        var bytes = new List<byte>([0, 0]);
        bytes.AddRange(BitConverter.GetBytes((ushort)valueLength));
        bytes.AddRange(BitConverter.GetBytes(type));
        bytes.AddRange(System.Text.Encoding.Unicode.GetBytes(key + "\0"));
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
}
