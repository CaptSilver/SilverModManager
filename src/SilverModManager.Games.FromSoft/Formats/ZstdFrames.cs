using ZstdSharp;
using ZstdSharp.Unsafe;

namespace SilverModManager.Games.FromSoft.Formats;

/// <summary>Zstandard frames written the way the game's own files are.</summary>
internal static class ZstdFrames
{
    /// <summary>The smallest window log Zstandard defines.</summary>
    public const int MinWindowLog = 10;

    /// <summary>
    /// The largest window log a frame may ask for. It is libzstd's own default decoding ceiling, and the largest
    /// window any shipped file uses (Nightreign's 27).
    /// </summary>
    public const int MaxWindowLog = 27;

    private static readonly byte[] Magic = [0x28, 0xB5, 0x2F, 0xFD];

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
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="windowLog"/> is outside <see cref="MinWindowLog"/>-<see cref="MaxWindowLog"/>.</exception>
    public static byte[] Compress(ReadOnlySpan<byte> payload, int level, int windowLog)
    {
        if (windowLog is < MinWindowLog or > MaxWindowLog)
            throw new ArgumentOutOfRangeException(nameof(windowLog), windowLog, $"The window log must be {MinWindowLog}-{MaxWindowLog}.");

        using var output = new MemoryStream();
        using (var stream = new CompressionStream(output, level, leaveOpen: true))
        {
            stream.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, windowLog);
            stream.Write(payload);
        }
        return output.ToArray();
    }

    /// <summary>Decompresses a frame that may not declare its size, reading at most <paramref name="limit"/> bytes.</summary>
    /// <exception cref="InvalidDataException">The header asks for a window larger than <see cref="MaxWindowLog"/>; nothing is allocated for it.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> frame, int limit)
    {
        RejectOversizedWindow(frame);

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

    /// <summary>
    /// Reads the window a frame header asks for, before a decoder sizes its buffers from it. A frame that sets the
    /// single-segment flag has no window descriptor; its window is its content size.
    /// </summary>
    private static void RejectOversizedWindow(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6 || !frame[..4].SequenceEqual(Magic)) return; // the decoder reports what is wrong with it

        var descriptor = frame[4];
        ulong size;
        if ((descriptor & 0x20) == 0)
        {
            // The descriptor is an exponent and a three-bit mantissa that adds eighths of the base size.
            var exponent = frame[5] >> 3;
            var mantissa = (ulong)(frame[5] & 7);
            var baseSize = 1UL << (MinWindowLog + exponent);
            size = baseSize + baseSize / 8 * mantissa;
        }
        else
        {
            var sizeBytes = (descriptor >> 6) switch { 0 => 1, 1 => 2, 2 => 4, _ => 8 };
            var offset = 5 + ((descriptor & 3) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 });
            if (frame.Length < offset + sizeBytes) return;

            size = 0;
            for (var i = sizeBytes - 1; i >= 0; i--) size = (size << 8) | frame[offset + i];
            if ((descriptor >> 6) == 1) size += 256;
        }

        if (size > 1UL << MaxWindowLog)
            throw new InvalidDataException($"the frame asks for a window of {size} bytes, more than the 2^{MaxWindowLog} the game's decoder allows");
    }
}
