using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;

namespace NexusMods.Games.Bethesda.Archives;

/// <summary>
/// The codecs the archive formats use and the stream helpers a builder needs around them.
/// </summary>
internal static class ArchiveCompression
{
    /// <summary>
    /// Copies <paramref name="source"/> to its end and returns how many bytes that was.
    /// </summary>
    public static async Task<long> CopyCounting(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
        }

        return total;
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes, or throws a <see cref="BethesdaArchiveException"/> naming
    /// <paramref name="what"/> when the stream ends first.
    /// </summary>
    public static async Task<byte[]> ReadExactly(Stream source, int count, string what, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        try
        {
            await source.ReadExactlyAsync(buffer, cancellationToken);
        }
        catch (EndOfStreamException e)
        {
            throw new BethesdaArchiveException($"{what} is shorter than the {count} byte(s) its state records", e);
        }

        return buffer;
    }

    /// <summary>A zlib stream, which is what versions 103 and 104 and BA2 expect.</summary>
    public static async Task<long> Deflate(Stream source, Stream destination, int level, CancellationToken cancellationToken)
    {
        long total;
        await using (var deflater = new DeflaterOutputStream(destination, new Deflater(level)) { IsStreamOwner = false })
        {
            total = await CopyCounting(source, deflater, cancellationToken);
        }

        return total;
    }

    /// <summary>An LZ4 frame, which is what Skyrim SE (version 105) expects.</summary>
    public static async Task<long> Lz4Frame(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        long total;
        await using (var encoder = LZ4Stream.Encode(destination, new LZ4EncoderSettings { CompressionLevel = LZ4Level.L12_MAX }, leaveOpen: true))
        {
            total = await CopyCounting(source, encoder, cancellationToken);
        }

        return total;
    }

    /// <summary>A single raw LZ4 block, which is what Starfield BA2 chunks hold.</summary>
    public static byte[] Lz4Block(byte[] data)
    {
        var target = new byte[LZ4Codec.MaximumOutputSize(data.Length)];
        var length = LZ4Codec.Encode(data, target, LZ4Level.L12_MAX);
        return target.AsSpan(0, length).ToArray();
    }
}
