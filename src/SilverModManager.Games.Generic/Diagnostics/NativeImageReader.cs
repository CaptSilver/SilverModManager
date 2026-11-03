using System.Diagnostics.CodeAnalysis;
using System.Reflection.PortableExecutable;
using System.Text;
using NexusMods.Paths;

namespace SilverModManager.Games.Generic.Diagnostics;

/// <summary>
/// Reads a PE file's import table and its <c>VS_FIXEDFILEINFO</c> directly. .NET's own version reader only knows
/// managed assemblies on Linux, so it cannot be used for a native DLL in a Wine prefix.
/// </summary>
public sealed class NativeImageReader : INativeImageReader
{
    private const int ImportDescriptorSize = 20;
    private const int MaxLibraryNameLength = 260;
    private const uint FixedFileInfoSignature = 0xFEEF04BD;

    /// <inheritdoc />
    public IReadOnlyList<string> ImportedLibraries(AbsolutePath file)
    {
        using var stream = file.Read();
        if (!TryOpen(stream, out var pe)) return [];
        using (pe)
        {
            var directory = pe.PEHeaders.PEHeader?.ImportTableDirectory;
            if (directory is not { Size: > 0 }) return [];

            var table = pe.GetSectionData(directory.Value.RelativeVirtualAddress);
            if (table.Length == 0) return [];

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
            return names;
        }
    }

    /// <inheritdoc />
    public Version? FileVersion(AbsolutePath file)
    {
        using var stream = file.Read();
        if (!TryOpen(stream, out var pe)) return null;
        using (pe)
        {
            var resources = pe.PEHeaders.PEHeader?.ResourceTableDirectory;
            if (resources is not { Size: > 0 }) return null;

            // The version block sits inside the resource section; its fixed part starts with a known signature.
            var section = pe.GetSectionData(resources.Value.RelativeVirtualAddress);
            if (section.Length == 0) return null;

            var bytes = section.GetContent().AsSpan();
            var signature = BitConverter.GetBytes(FixedFileInfoSignature);
            var offset = 0;
            while (offset <= bytes.Length - 16)
            {
                var found = bytes[offset..].IndexOf(signature);
                if (found < 0 || offset + found + 16 > bytes.Length) return null;

                var at = offset + found;
                offset = at + 1;
                if (BitConverter.ToUInt32(bytes[(at + 4)..]) != 0x00010000) continue; // dwStrucVersion

                var ms = BitConverter.ToUInt32(bytes[(at + 8)..]);
                var ls = BitConverter.ToUInt32(bytes[(at + 12)..]);
                return new Version((int)(ms >> 16), (int)(ms & 0xFFFF), (int)(ls >> 16), (int)(ls & 0xFFFF));
            }
            return null;
        }
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
