using System.Text;

namespace SilverModManager.Games.Generic.Tests;

/// <summary>
/// Builds the smallest 64-bit Windows image the readers accept: an import section naming the given libraries and a
/// resource section that holds a <c>VS_FIXEDFILEINFO</c> for the given version. Fixtures in code, so no test needs a
/// real DLL.
/// </summary>
internal static class SyntheticPe
{
    private const int FileAlignment = 0x200;
    private const int SectionAlignment = 0x1000;
    private const int ImportRva = 0x1000;
    private const int ImportRaw = 0x400;
    private const int ResourceRva = 0x2000;
    private const int ResourceRaw = 0x600;
    private const int SectionSize = 0x200;

    public static byte[] Build(string[] imports, Version? version = null)
    {
        var image = new byte[ResourceRaw + SectionSize];
        using var stream = new MemoryStream(image);
        using var w = new BinaryWriter(stream);

        // DOS header: "MZ" and the offset of the PE header.
        w.Write((ushort)0x5A4D);
        stream.Position = 0x3C;
        w.Write(0x40);

        stream.Position = 0x40;
        w.Write(0x00004550u); // "PE\0\0"
        w.Write((ushort)0x8664); // x64
        w.Write((ushort)2); // sections
        w.Write(0u); w.Write(0u); w.Write(0u); // time stamp, symbol table, symbol count
        w.Write((ushort)240); // optional header size
        w.Write((ushort)0x2022); // executable, large address aware, DLL

        // Optional header (PE32+).
        w.Write((ushort)0x20B);
        w.Write((ushort)0); // linker version
        w.Write(0u); w.Write(0u); w.Write(0u); // sizes of code, data, bss
        w.Write(0u); w.Write(0u); // entry point, base of code
        w.Write(0x180000000UL); // image base
        w.Write(SectionAlignment);
        w.Write(FileAlignment);
        w.Write(new byte[8]); // OS version, image version
        w.Write((ushort)6); w.Write((ushort)0); // subsystem version
        w.Write(0u); // win32 version
        w.Write(0x3000); // size of image
        w.Write(0x400); // size of headers
        w.Write(0u); // checksum
        w.Write((ushort)3); // console subsystem
        w.Write((ushort)0); // DLL characteristics
        w.Write(0x100000UL); w.Write(0x1000UL); w.Write(0x100000UL); w.Write(0x1000UL); // stack and heap sizes
        w.Write(0u); // loader flags
        w.Write(16); // data directory count
        for (var i = 0; i < 16; i++)
        {
            var (rva, size) = i switch
            {
                1 => (ImportRva, 0x100),
                2 => (ResourceRva, 0x100),
                _ => (0, 0),
            };
            w.Write(rva);
            w.Write(size);
        }

        WriteSection(w, ".idata", ImportRva, ImportRaw);
        WriteSection(w, ".rsrc", ResourceRva, ResourceRaw);

        // One import descriptor per library, then the null descriptor; the names follow.
        stream.Position = ImportRaw;
        var nameRva = ImportRva + 0x80;
        foreach (var import in imports)
        {
            w.Write(0); w.Write(0); w.Write(0); w.Write(nameRva); w.Write(0);
            var at = stream.Position;
            stream.Position = ImportRaw + (nameRva - ImportRva);
            w.Write(Encoding.ASCII.GetBytes(import + "\0"));
            stream.Position = at;
            nameRva += 0x20;
        }

        if (version is not null)
        {
            stream.Position = ResourceRaw + 0x10;
            w.Write(0xFEEF04BDu);
            w.Write(0x00010000u);
            w.Write((uint)(version.Major << 16 | version.Minor));
            w.Write((uint)(version.Build << 16 | version.Revision));
        }

        w.Flush();
        return image;
    }

    private static void WriteSection(BinaryWriter w, string name, int rva, int raw)
    {
        w.Write(Encoding.ASCII.GetBytes(name.PadRight(8, '\0')));
        w.Write(SectionSize); // virtual size
        w.Write(rva);
        w.Write(SectionSize); // raw size
        w.Write(raw);
        w.Write(0u); w.Write(0u); // relocations, line numbers
        w.Write((ushort)0); w.Write((ushort)0);
        w.Write(0x40000040u); // initialised data, readable
    }
}
