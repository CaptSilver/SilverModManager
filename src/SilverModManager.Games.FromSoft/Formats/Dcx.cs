using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ZstdSharp;

namespace SilverModManager.Games.FromSoft.Formats;

/// <summary>
/// The DCX container: the compression wrapper around packed game archives. Reading takes Kraken, zlib and
/// Zstandard, whichever the game ships. Writing emits zlib or Zstandard and never Kraken, which has no open encoder
/// and which the game reads no differently from the other two.
/// </summary>
public static class Dcx
{
    public const int HeaderSize = 0x4C;

    public static ReadOnlySpan<byte> MagicDcx => "DCX\0"u8;

    public static ReadOnlySpan<byte> MagicDcs => "DCS\0"u8;

    public static ReadOnlySpan<byte> MagicDcp => "DCP\0"u8;

    public static ReadOnlySpan<byte> MagicDca => "DCA\0"u8;

    // The game validates the level byte as 1-9 before it dispatches to the zlib branch. It is not a general DCX rule:
    // shipped Zstandard regulations carry 15 and 21 in the same byte.
    private const int MinZlibLevel = 1;
    private const int MaxZlibLevel = 9;

    private const int MinZstdLevel = 1;
    private const int MaxZstdLevel = 22;

    // Offsets of the fields the reader and writer share.
    private const int UncompressedSizeOffset = 0x1C;
    private const int CompressedSizeOffset = 0x20;
    private const int MethodOffset = 0x28;
    private const int LevelOffset = 0x30;

    /// <summary>Decompresses a DCX container and returns its payload.</summary>
    /// <exception cref="DcxException">The container is malformed, or its method is not one the game ships.</exception>
    public static byte[] Read(ReadOnlySpan<byte> data, FromSoftFormatTraits traits)
    {
        if (data.Length < MagicDcx.Length || !data[..MagicDcx.Length].SequenceEqual(MagicDcx))
            throw new DcxException("not a DCX container (bad magic)");
        if (data.Length < HeaderSize)
            throw new DcxException($"DCX header is truncated: need {HeaderSize} bytes, file holds {data.Length}");

        var uncompressed = BinaryPrimitives.ReadUInt32BigEndian(data[UncompressedSizeOffset..]);
        var compressed = BinaryPrimitives.ReadUInt32BigEndian(data[CompressedSizeOffset..]);
        var method = Encoding.ASCII.GetString(data.Slice(MethodOffset, 4));

        var available = data.Length - HeaderSize;
        if (compressed > available)
            throw new DcxException($"DCX is truncated: header claims {compressed} compressed bytes, file holds {available}");
        var body = data.Slice(HeaderSize, (int)compressed);

        // Gated once, before any decoder: a decoder may size its output buffer straight from this field.
        if (uncompressed > Kraken.MaxUncompressedSize)
            throw new DcxException($"refusing to allocate {uncompressed} bytes for a DCX payload, exceeds the {Kraken.MaxUncompressedSize}-byte cap");
        var size = (int)uncompressed;

        var compression = method switch
        {
            "KRAK" => DcxCompression.Kraken,
            "DFLT" => DcxCompression.Dflt,
            "ZSTD" => DcxCompression.Zstd,
            _ => throw new DcxException($"unsupported DCX compression '{method}'"),
        };
        if (!traits.ReadableCompressions.Contains(compression))
            throw new DcxException($"this game does not ship {method} files; the methods it reads are {string.Join(", ", traits.ReadableCompressions)}");

        return compression switch
        {
            DcxCompression.Kraken => ReadKraken(body, size),
            DcxCompression.Dflt => ReadDflt(body, size),
            _ => ReadZstd(body, size),
        };
    }

    /// <summary>Wraps <paramref name="payload"/> in a container of the given format.</summary>
    /// <exception cref="DcxException">The compression cannot be written, or the level is outside what the game accepts.</exception>
    public static byte[] Write(ReadOnlySpan<byte> payload, DcxWriteFormat format)
    {
        byte[] body;
        switch (format.Compression)
        {
            case DcxCompression.Dflt:
                // Checked before compressing: the game rejects the container at load, not at write time.
                if (format.Level is < MinZlibLevel or > MaxZlibLevel)
                    throw new DcxException($"zlib level {format.Level} is outside {MinZlibLevel}-{MaxZlibLevel}; the game's DCX reader rejects the container before decompressing it");
                body = WriteDflt(payload, format.Level);
                break;
            case DcxCompression.Zstd:
                if (format.Level is < MinZstdLevel or > MaxZstdLevel)
                    throw new DcxException($"Zstandard level {format.Level} is outside {MinZstdLevel}-{MaxZstdLevel}");
                body = ZstdFrames.Compress(payload, format.Level, format.WindowLog);
                break;
            case DcxCompression.Kraken:
                throw new DcxException("KRAK cannot be written: there is no open Kraken encoder");
            default:
                throw new DcxException($"{format.Compression} is not a DCX compression that can be written");
        }

        var method = format.Compression == DcxCompression.Dflt ? "DFLT"u8 : "ZSTD"u8;
        var blob = new byte[HeaderSize + body.Length];
        WriteHeader(blob, method, format, payload.Length, body.Length);
        body.CopyTo(blob, HeaderSize);
        return blob;
    }

    private static void WriteHeader(Span<byte> header, ReadOnlySpan<byte> method, DcxWriteFormat format, int uncompressed, int compressed)
    {
        MagicDcx.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header[0x04..], format.Unk04);
        BinaryPrimitives.WriteUInt32BigEndian(header[0x08..], 0x18);
        BinaryPrimitives.WriteUInt32BigEndian(header[0x0C..], 0x24);
        BinaryPrimitives.WriteUInt32BigEndian(header[0x10..], 0x44);
        BinaryPrimitives.WriteUInt32BigEndian(header[0x14..], HeaderSize);
        MagicDcs.CopyTo(header[0x18..]);
        BinaryPrimitives.WriteUInt32BigEndian(header[UncompressedSizeOffset..], (uint)uncompressed);
        BinaryPrimitives.WriteUInt32BigEndian(header[CompressedSizeOffset..], (uint)compressed);
        MagicDcp.CopyTo(header[0x24..]);
        method.CopyTo(header[MethodOffset..]);
        BinaryPrimitives.WriteUInt32BigEndian(header[0x2C..], 0x20);
        header[LevelOffset] = (byte)format.Level;
        // 0x31..0x3F stay zero. The word after them is fixed in every shipped archive.
        BinaryPrimitives.WriteUInt32BigEndian(header[0x40..], 0x00010100);
        MagicDca.CopyTo(header[0x44..]);
        BinaryPrimitives.WriteUInt32BigEndian(header[0x48..], 8);
    }

    private static byte[] ReadKraken(ReadOnlySpan<byte> body, int size)
    {
        try
        {
            return Kraken.Decompress(body, size);
        }
        catch (KrakenException e)
        {
            throw new DcxException($"KRAK body could not be decompressed: {e.Message}");
        }
    }

    private static byte[] ReadDflt(ReadOnlySpan<byte> body, int size)
    {
        byte[] output;
        try
        {
            output = Inflate(body, size);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            // Callers catch DcxException, and a corrupt body is a malformed container, not a programming fault.
            throw new DcxException($"DFLT body could not be decompressed: {e.Message}");
        }
        return EnsureSize("DFLT", output, size);
    }

    private static byte[] ReadZstd(ReadOnlySpan<byte> body, int size)
    {
        byte[] output;
        try
        {
            // One byte past the claim, so a body that decompresses to more than the header says is seen as too long.
            output = ZstdFrames.Decompress(body, size + 1);
        }
        catch (Exception e) when (e is ZstdException or IOException or InvalidDataException)
        {
            throw new DcxException($"ZSTD body could not be decompressed: {e.Message}");
        }
        return EnsureSize("ZSTD", output, size);
    }

    private static byte[] Inflate(ReadOnlySpan<byte> body, int size)
    {
        using var input = new MemoryStream(body.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var buffer = new byte[size + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = zlib.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        Array.Resize(ref buffer, total);
        return buffer;
    }

    private static byte[] EnsureSize(string method, byte[] output, int size)
    {
        // The decoders stop one byte past the claim, so a longer payload shows up only as "longer", not its real size.
        if (output.Length > size)
            throw new DcxException($"{method} payload is longer than the {size} bytes the header claims");
        if (output.Length < size)
            throw new DcxException($"{method} payload is {output.Length} bytes, header claims {size}");
        return output;
    }

    private static byte[] WriteDflt(ReadOnlySpan<byte> payload, int level)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, new ZLibCompressionOptions { CompressionLevel = level }, leaveOpen: true))
            zlib.Write(payload);
        return output.ToArray();
    }
}
