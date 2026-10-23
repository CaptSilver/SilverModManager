using System.Runtime.InteropServices;

namespace SilverModManager.Games.FromSoft.Formats.Native;

// Linux only, like the rest of the app. The library is built from extern/ooz by scripts/build-ooz.sh.
internal static unsafe partial class LibOoz
{
    // Resolved by LibOozResolver: the file ships under runtimes/linux-x64/native, not on the loader's search path.
    internal const string LibraryName = "ooz";

    static LibOoz() => LibOozResolver.Install();

    /// <summary>
    /// Decodes a Kraken stream whose 256 KiB chunks are coded independently. Returns the number of bytes written,
    /// -1 when a chunk fails to decode and -2 when a chunk is shorter than it declares.
    /// </summary>
    [LibraryImport(LibraryName)]
    internal static partial int ooz_decompress_seekchunked(byte* src, nuint srcLen, byte* dst, nuint dstLen);
}
