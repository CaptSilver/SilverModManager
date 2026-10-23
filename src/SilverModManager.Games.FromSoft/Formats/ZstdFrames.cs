using ZstdSharp;
using ZstdSharp.Unsafe;

namespace SilverModManager.Games.FromSoft.Formats;

/// <summary>Zstandard frames written the way the game's own files are.</summary>
internal static class ZstdFrames
{
    /// <summary>
    /// Compresses <paramref name="payload"/> as a frame with no content size in its header.
    /// </summary>
    /// <remarks>
    /// The payload is fed with "continue" and the frame closed with "end" in a separate step. Handing the whole input
    /// over with "end" lets the encoder see the size and record it (bit 7 of the frame header descriptor), which no
    /// shipped file does; the bytes still decompress, but the frame is unlike anything the game has loaded.
    /// The window log is a compression parameter, not a ratio knob: it is what the decoder must allocate, and a
    /// frame asking for a large window crashes the game on load.
    /// </remarks>
    public static byte[] Compress(ReadOnlySpan<byte> payload, int level, int windowLog)
    {
        using var output = new MemoryStream();
        using (var stream = new CompressionStream(output, level, leaveOpen: true))
        {
            stream.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, windowLog);
            stream.Write(payload);
        }
        return output.ToArray();
    }

    /// <summary>Decompresses a frame that may not declare its size, reading at most <paramref name="limit"/> bytes.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> frame, int limit)
    {
        using var input = new MemoryStream(frame.ToArray());
        using var stream = new DecompressionStream(input);
        var buffer = new byte[limit];
        var total = 0;
        while (total < limit)
        {
            var read = stream.Read(buffer, total, limit - total);
            if (read == 0) break;
            total += read;
        }
        Array.Resize(ref buffer, total);
        return buffer;
    }
}
