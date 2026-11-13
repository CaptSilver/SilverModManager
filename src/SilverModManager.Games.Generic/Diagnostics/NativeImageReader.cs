using System.Diagnostics.CodeAnalysis;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using System.Text;
using NexusMods.Paths;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Games.Generic.Diagnostics;

/// <summary>
/// Reads a PE file's import table and bitness with <see cref="PEReader"/>, and its version through
/// <see cref="PeFileVersion"/>.
/// </summary>
public sealed class NativeImageReader : INativeImageReader
{
    private const int ImportDescriptorSize = 20;
    private const int DelayImportDescriptorSize = 32;
    private const int MaxLibraryNameLength = 260;

    /// <inheritdoc />
    public NativeImageImports ReadImports(AbsolutePath file)
    {
        using var stream = file.Read();
        if (!TryOpen(stream, out var pe)) return NativeImageImports.None;
        using (pe)
        {
            if (pe.PEHeaders.PEHeader is not { } header) return NativeImageImports.None;
            var is64Bit = header.Magic == PEMagic.PE32Plus;

            var imageBase = header.ImageBase;
            return new NativeImageImports(
                ReadDescriptorNames(pe, header.ImportTableDirectory, ImportDescriptorSize, nameOffset: 12, (_, name) => name),
                is64Bit,
                ReadDescriptorNames(pe, header.DelayImportTableDirectory, DelayImportDescriptorSize, nameOffset: 4,
                    (attributes, name) => DelayNameRva(attributes, name, imageBase)));
        }
    }

    // A delay-load descriptor's first field holds attributes; bit 0 says the other fields are RVAs. Linkers before
    // Visual C++ 7 left it clear and wrote virtual addresses, which only fit in a 32-bit image's address space.
    private static int DelayNameRva(int attributes, int name, ulong imageBase)
        => (attributes & 1) != 0 ? name : unchecked((int)((uint)name - (uint)imageBase));

    // Both tables are an array of fixed-size descriptors ended by one whose name field is zero; they differ in size,
    // in where the name sits and in how it is addressed. toRva gets the descriptor's first field and the name field.
    private static List<string> ReadDescriptorNames(PEReader pe, DirectoryEntry directory, int descriptorSize, int nameOffset, Func<int, int, int> toRva)
    {
        var names = new List<string>();
        if (directory.Size <= 0) return names;

        var table = pe.GetSectionData(directory.RelativeVirtualAddress);
        if (table.Length == 0) return names;

        var reader = table.GetReader();
        while (reader.RemainingBytes >= descriptorSize)
        {
            var start = reader.Offset;
            var first = reader.ReadInt32();
            reader.Offset = start + nameOffset;
            var name = reader.ReadInt32();
            if (name == 0) break;
            reader.Offset = start + descriptorSize;

            // A name outside every section reads as empty; it names nothing the loader could find.
            if (ReadName(pe, toRva(first, name)) is { Length: > 0 } library) names.Add(library);
        }
        return names;
    }

    /// <inheritdoc />
    public Version? FileVersion(AbsolutePath file)
    {
        using var stream = file.Read();
        return PeFileVersion.ReadVersion(stream);
    }

    private static bool TryOpen(Stream stream, [NotNullWhen(true)] out PEReader? reader)
    {
        try
        {
            reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (reader.PEHeaders.PEHeader is not null) return true;
            reader.Dispose();
        }
        catch (BadImageFormatException)
        {
            // Not a Windows image: nothing to read.
        }
        reader = null;
        return false;
    }

    private static string ReadName(PEReader pe, int rva)
    {
        if (rva < 0) return string.Empty;
        var reader = pe.GetSectionData(rva).GetReader();
        var bytes = new List<byte>();
        while (reader.RemainingBytes > 0 && bytes.Count < MaxLibraryNameLength)
        {
            var next = reader.ReadByte();
            if (next == 0) break;
            bytes.Add(next);
        }
        return Encoding.ASCII.GetString([.. bytes]);
    }
}
