// Follows esplugin src/record.rs (record_header), src/subrecord.rs (subrecord framing, XXXX) and src/plugin.rs (header fields).
using System.Buffers.Binary;
using System.Text;

namespace SilverModManager.Games.Bethesda.LoadOrder;

/// <summary>Reads the header record (TES3 or TES4) of a plugin. Nothing past that record is touched.</summary>
public static class PluginHeaderReader
{
    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)
        ?? throw new InvalidOperationException("The Windows-1252 code page is not available");

    private const int Tes3HedrAuthorOffset = 8;
    private const int Tes3HedrAuthorLength = 32;
    private const int Tes3HedrDescriptionOffset = 40;
    private const int Tes3HedrDescriptionLength = 256;
    private const int Tes3HedrRecordCountOffset = 296;

    /// <exception cref="InvalidPluginException">The stream is truncated or does not start with the format's header record.</exception>
    public static PluginHeader Read(Stream stream, PluginFormat format, string fileName)
    {
        var tes3 = format == PluginFormat.Tes3;
        var expectedType = tes3 ? "TES3"u8 : "TES4"u8;
        var headerLength = format switch
        {
            PluginFormat.Tes3 => 16,
            PluginFormat.Tes4Oblivion => 20,
            _ => 24,
        };

        var recordHeader = ReadExactly(stream, headerLength, fileName, "record header");
        if (!recordHeader.AsSpan(0, 4).SequenceEqual(expectedType))
        {
            throw new InvalidPluginException($"{fileName}: expected a {Encoding.ASCII.GetString(expectedType)} header record but found '{Printable(recordHeader.AsSpan(0, 4))}'");
        }

        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader.AsSpan(4));
        // Tes3 has 4 unknown bytes between the size and the flags.
        var headerFlags = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader.AsSpan(tes3 ? 12 : 8));
        var data = ReadExactly(stream, dataSize, fileName, "header record data");

        var masters = new List<string>();
        byte[]? hedr = null, cnam = null, snam = null;
        foreach (var (type, bytes) in Subrecords(data, tes3, fileName))
        {
            switch (type)
            {
                case "MAST": masters.Add(DecodeString(bytes)); break;
                case "HEDR": hedr ??= bytes; break;
                case "CNAM" when !tes3: cnam ??= bytes; break;
                case "SNAM" when !tes3: snam ??= bytes; break;
            }
        }

        float? version = hedr is { Length: >= 4 } ? BinaryPrimitives.ReadSingleLittleEndian(hedr) : null;

        if (tes3)
        {
            // The master bit sits in HEDR's own flag word; the record header's flags are unused.
            var flags = hedr is { Length: >= 8 } ? BinaryPrimitives.ReadUInt32LittleEndian(hedr.AsSpan(4)) : 0;
            return new PluginHeader(fileName, masters, flags, version,
                UInt32At(hedr, Tes3HedrRecordCountOffset),
                Slice(hedr, Tes3HedrDescriptionOffset, Tes3HedrDescriptionLength),
                Slice(hedr, Tes3HedrAuthorOffset, Tes3HedrAuthorLength),
                format);
        }

        return new PluginHeader(fileName, masters, headerFlags, version,
            UInt32At(hedr, 4),
            snam is null ? null : DecodeString(snam),
            cnam is null ? null : DecodeString(cnam),
            format);
    }

    private static IEnumerable<(string Type, byte[] Data)> Subrecords(byte[] data, bool wideLength, string fileName)
    {
        var position = 0;
        uint? presized = null;
        while (position < data.Length)
        {
            if (data.Length - position < 4 + (wideLength ? 4 : 2)) throw Truncated(fileName, "subrecord header");
            var type = Encoding.ASCII.GetString(data, position, 4);
            var length = wideLength
                ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 4));
            position += 4 + (wideLength ? 4 : 2);

            // After an XXXX record the next subrecord's length field is a placeholder; XXXX holds the real size.
            if (!wideLength && presized is { } real) length = real;
            presized = null;

            if (length > data.Length - position) throw Truncated(fileName, $"{type} subrecord");
            var body = data.AsSpan(position, (int)length).ToArray();
            position += (int)length;

            if (!wideLength && type == "XXXX")
            {
                if (body.Length < 4) throw Truncated(fileName, "XXXX subrecord");
                presized = BinaryPrimitives.ReadUInt32LittleEndian(body);
                continue;
            }

            yield return (type, body);
        }
    }

    private static uint? UInt32At(byte[]? hedr, int offset)
        => hedr is not null && hedr.Length >= offset + 4 ? BinaryPrimitives.ReadUInt32LittleEndian(hedr.AsSpan(offset)) : null;

    /// <summary>A fixed-width, NUL-padded text field of a Tes3 HEDR; null when HEDR is missing or too short to hold it.</summary>
    private static string? Slice(byte[]? hedr, int offset, int length)
        => hedr is not null && hedr.Length >= offset + length ? DecodeString(hedr.AsSpan(offset, length)) : null;

    private static string DecodeString(ReadOnlySpan<byte> bytes)
    {
        var nul = bytes.IndexOf((byte)0);
        return Windows1252.GetString(nul < 0 ? bytes : bytes[..nul]);
    }

    private static string Printable(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) chars[i] = bytes[i] is >= 0x20 and < 0x7f ? (char)bytes[i] : '?';
        return new string(chars);
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes without trusting the count: a corrupt size field
    /// must not allocate gigabytes, so the buffer grows only as bytes actually arrive.
    /// </summary>
    private static byte[] ReadExactly(Stream stream, long count, string fileName, string what)
    {
        const int chunk = 64 * 1024;
        if (count <= chunk)
        {
            var small = new byte[count];
            if (stream.ReadAtLeast(small, small.Length, throwOnEndOfStream: false) < small.Length) throw Truncated(fileName, what);
            return small;
        }

        using var buffer = new MemoryStream();
        var scratch = new byte[chunk];
        while (buffer.Length < count)
        {
            var wanted = (int)Math.Min(chunk, count - buffer.Length);
            var read = stream.ReadAtLeast(scratch.AsSpan(0, wanted), wanted, throwOnEndOfStream: false);
            if (read < wanted) throw Truncated(fileName, what);
            buffer.Write(scratch, 0, read);
        }
        return buffer.ToArray();
    }

    private static InvalidPluginException Truncated(string fileName, string what)
        => new($"{fileName}: plugin ends inside the {what}");
}
