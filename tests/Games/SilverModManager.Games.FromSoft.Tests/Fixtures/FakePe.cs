using System.Text;

namespace SilverModManager.Games.FromSoft.Tests.Fixtures;

/// <summary>
/// A minimal but well-formed 64-bit PE file whose only section is a version resource. The fixed file info carries
/// the given file and product version and, when asked, the string table a version reader may look at as well.
/// </summary>
internal static class FakePe
{
    private const int HeaderSize = 0x200;
    private const int SectionRva = 0x1000;

    public static byte[] WithVersion(Version version, string? productVersionString = null)
    {
        var info = VersionInfo(version, productVersionString);
        var resource = ResourceSection(info);

        var image = new byte[HeaderSize + Align(resource.Length, 0x200)];
        var span = image.AsSpan();

        // DOS header: "MZ" and the offset of the PE header.
        span[0] = (byte)'M';
        span[1] = (byte)'Z';
        BitConverter.TryWriteBytes(span[0x3C..], 0x40);

        // PE signature and COFF header: x86-64, one section, a 240-byte optional header.
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(span[0x40..]);
        BitConverter.TryWriteBytes(span[0x44..], (ushort)0x8664);
        BitConverter.TryWriteBytes(span[0x46..], (ushort)1);
        BitConverter.TryWriteBytes(span[0x54..], (ushort)240);
        BitConverter.TryWriteBytes(span[0x56..], (ushort)0x0022);

        // Optional header (PE32+).
        var optional = span[0x58..];
        BitConverter.TryWriteBytes(optional, (ushort)0x20B);
        BitConverter.TryWriteBytes(optional[16..], SectionRva); // entry point, inside the section
        BitConverter.TryWriteBytes(optional[24..], 0x140000000UL);
        BitConverter.TryWriteBytes(optional[32..], 0x1000); // section alignment
        BitConverter.TryWriteBytes(optional[36..], 0x200); // file alignment
        BitConverter.TryWriteBytes(optional[40..], (ushort)6); // OS version
        BitConverter.TryWriteBytes(optional[48..], (ushort)6); // subsystem version
        BitConverter.TryWriteBytes(optional[56..], SectionRva + Align(resource.Length, 0x1000)); // size of image
        BitConverter.TryWriteBytes(optional[60..], HeaderSize);
        BitConverter.TryWriteBytes(optional[68..], (ushort)2); // GUI subsystem
        BitConverter.TryWriteBytes(optional[108..], 16); // number of data directories
        BitConverter.TryWriteBytes(optional[(112 + 2 * 8)..], SectionRva); // resource directory
        BitConverter.TryWriteBytes(optional[(112 + 2 * 8 + 4)..], resource.Length);

        // Section header for .rsrc.
        var section = span[(0x58 + 240)..];
        Encoding.ASCII.GetBytes(".rsrc").CopyTo(section);
        BitConverter.TryWriteBytes(section[8..], resource.Length);
        BitConverter.TryWriteBytes(section[12..], SectionRva);
        BitConverter.TryWriteBytes(section[16..], Align(resource.Length, 0x200));
        BitConverter.TryWriteBytes(section[20..], HeaderSize);
        BitConverter.TryWriteBytes(section[36..], 0x40000040u); // initialised data, readable

        resource.CopyTo(span[HeaderSize..]);
        return image;
    }

    /// <summary>Resource directory: type 16 (version), name 1, language 0x409, one data entry, then the data.</summary>
    private static byte[] ResourceSection(byte[] info)
    {
        const int typeDirectory = 0x18;
        const int nameDirectory = 0x30;
        const int dataEntry = 0x48;
        const int data = 0x58;

        var bytes = new byte[data + info.Length];
        var span = bytes.AsSpan();

        BitConverter.TryWriteBytes(span[12..], (ushort)1); // one id entry
        BitConverter.TryWriteBytes(span[16..], 16u);
        BitConverter.TryWriteBytes(span[20..], 0x80000000u | typeDirectory);

        BitConverter.TryWriteBytes(span[(typeDirectory + 12)..], (ushort)1);
        BitConverter.TryWriteBytes(span[(typeDirectory + 16)..], 1u);
        BitConverter.TryWriteBytes(span[(typeDirectory + 20)..], 0x80000000u | nameDirectory);

        BitConverter.TryWriteBytes(span[(nameDirectory + 12)..], (ushort)1);
        BitConverter.TryWriteBytes(span[(nameDirectory + 16)..], 0x409u);
        BitConverter.TryWriteBytes(span[(nameDirectory + 20)..], (uint)dataEntry);

        BitConverter.TryWriteBytes(span[dataEntry..], SectionRva + data);
        BitConverter.TryWriteBytes(span[(dataEntry + 4)..], info.Length);

        info.CopyTo(span[data..]);
        return bytes;
    }

    /// <summary>A VS_VERSIONINFO block: the root key, VS_FIXEDFILEINFO, and optionally a StringFileInfo table.</summary>
    private static byte[] VersionInfo(Version version, string? productVersionString)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode);

        writer.Write((ushort)0); // length, patched below
        writer.Write((ushort)52); // size of the fixed file info
        writer.Write((ushort)0); // binary
        writer.Write(Encoding.Unicode.GetBytes("VS_VERSION_INFO\0"));
        Pad(writer);

        writer.Write(0xFEEF04BDu);
        writer.Write(0x00010000u);
        writer.Write((uint)(version.Major << 16 | version.Minor));
        writer.Write((uint)(version.Build << 16 | Math.Max(version.Revision, 0)));
        writer.Write((uint)(version.Major << 16 | version.Minor));
        writer.Write((uint)(version.Build << 16 | Math.Max(version.Revision, 0)));
        writer.Write(0x3Fu); // flags mask
        writer.Write(0u); // flags
        writer.Write(0x00040004u); // NT, Win32
        writer.Write(1u); // application
        writer.Write(0u); // subtype
        writer.Write(0u);
        writer.Write(0u);

        if (productVersionString is not null) StringFileInfo(writer, productVersionString);

        writer.Flush();
        var bytes = stream.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(), (ushort)bytes.Length);
        return bytes;
    }

    private static void StringFileInfo(BinaryWriter writer, string productVersion)
    {
        Block(writer, "StringFileInfo", valueLength: 0, isText: true, () =>
            Block(writer, "040904b0", valueLength: 0, isText: true, () =>
                Block(writer, "ProductVersion", productVersion.Length + 1, isText: true, () =>
                {
                    writer.Write(Encoding.Unicode.GetBytes(productVersion + "\0"));
                })));
    }

    private static void Block(BinaryWriter writer, string key, int valueLength, bool isText, Action body)
    {
        Pad(writer);
        var start = writer.BaseStream.Position;
        writer.Write((ushort)0);
        writer.Write((ushort)valueLength);
        writer.Write((ushort)(isText ? 1 : 0));
        writer.Write(Encoding.Unicode.GetBytes(key + "\0"));
        Pad(writer);
        body();
        var end = writer.BaseStream.Position;
        writer.BaseStream.Position = start;
        writer.Write((ushort)(end - start));
        writer.BaseStream.Position = end;
    }

    private static void Pad(BinaryWriter writer)
    {
        while (writer.BaseStream.Position % 4 != 0) writer.Write((byte)0);
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
}
