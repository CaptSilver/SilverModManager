using SilverModManager.Games.FromSoft.Formats.Native;

namespace SilverModManager.Games.FromSoft.Formats;

/// <summary>The Kraken decoder refused a stream, or produced something other than the size it was asked for.</summary>
public class KrakenException(string message) : Exception(message);

/// <summary>
/// Oodle Kraken decompression, through ooz (extern/ooz, built into libooz.so by scripts/build-ooz.sh).
/// There is no open Kraken encoder and nothing here writes <c>KRAK</c>: the game also reads zlib, so a merged
/// archive is written with that instead.
/// </summary>
/// <remarks>
/// Trust boundary: upstream ooz says of itself "not fuzz safe, so please trust the input", and the decoder runs
/// in this process. Kraken is therefore decoded only from the game's own archives and from files of mods the
/// user has installed and named in a merge declaration. A mod the user installs already runs native code
/// through the mod loader, so this is trust they have extended anyway. Nothing scans arbitrary archives with it.
/// If that boundary ever widens, decoding belongs in a short-lived helper process; this is the one entry point to move.
/// </remarks>
public static class Kraken
{
    /// <summary>
    /// The largest uncompressed size accepted. A DCX header carries a bare 32-bit size straight from the file with
    /// nothing checking it against reality, and upstream ooz's own command line stops at the same figure. Real game
    /// data is far below it; the cap only keeps a corrupt header from asking for a multi-gigabyte buffer.
    /// </summary>
    public const int MaxUncompressedSize = 1024 * 1024 * 1024;

    // The decoder may write past the logical end of the last chunk.
    private const int OutputSlack = 64 * 1024;

    // The header parse reads two bytes before it checks the length, so an empty or one-byte stream would read past the
    // buffer. Zero padding turns that into a refused header.
    private const int InputPadding = 16;

    /// <summary>Decodes <paramref name="src"/> to exactly <paramref name="uncompressedSize"/> bytes, or throws.</summary>
    /// <remarks>A short result is never returned: a truncated payload would parse as a valid but cut-off archive that nothing later catches.</remarks>
    public static unsafe byte[] Decompress(ReadOnlySpan<byte> src, int uncompressedSize)
    {
        if (uncompressedSize < 0 || uncompressedSize > MaxUncompressedSize)
            throw new KrakenException($"refusing to allocate {uncompressedSize} bytes for a Kraken decode, outside the 0 to {MaxUncompressedSize} byte range");

        var padded = new byte[src.Length + InputPadding];
        src.CopyTo(padded);
        var dst = new byte[uncompressedSize + OutputSlack];

        int returned;
        fixed (byte* srcPtr = padded)
        fixed (byte* dstPtr = dst)
        {
            returned = LibOoz.ooz_decompress_seekchunked(srcPtr, (nuint)src.Length, dstPtr, (nuint)uncompressedSize);
        }

        EnsureExact(returned, uncompressedSize);
        Array.Resize(ref dst, uncompressedSize);
        return dst;
    }

    internal static void EnsureExact(int returned, int expected)
    {
        if (returned == expected) return;

        var reason = returned switch
        {
            -1 => "decode failed",
            -2 => "decode stalled",
            < 0 => $"returned sentinel {returned}",
            _ => $"produced {returned} bytes",
        };
        throw new KrakenException($"Kraken {reason}, expected {expected}: the data is truncated, or it is not a Kraken stream");
    }
}
