using System.Diagnostics.CodeAnalysis;
using System.Reflection.PortableExecutable;
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

            var directory = header.ImportTableDirectory;
            if (directory is not { Size: > 0 }) return new NativeImageImports([], is64Bit);

            var table = pe.GetSectionData(directory.RelativeVirtualAddress);
            if (table.Length == 0) return new NativeImageImports([], is64Bit);

            var names = new List<string>();
            var reader = table.GetReader();
            while (reader.RemainingBytes >= ImportDescriptorSize)
            {
                reader.ReadInt32(); // OriginalFirstThunk
                reader.ReadInt32(); // TimeDateStamp
                reader.ReadInt32(); // ForwarderChain
                var nameRva = reader.ReadInt32();
                reader.ReadInt32(); // FirstThunk
                if (nameRva == 0) break;

                names.Add(ReadName(pe, nameRva));
            }
            return new NativeImageImports(names, is64Bit);
        }
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
