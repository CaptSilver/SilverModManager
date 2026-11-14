using System.Buffers.Binary;
using System.Text;

namespace SilverModManager.Platform.Proton.Steam.BinaryKeyValues;

/// <summary>
/// The binary KeyValues form <c>shortcuts.vdf</c> uses: a type byte, a NUL-terminated key, then a value. Only the
/// three types Steam writes there are known (0x00 map, 0x01 string, 0x02 int32); anything else is a refusal,
/// because skipping a value we cannot read would drop it on the next save.
/// </summary>
public sealed class HandWrittenBinaryKeyValues : IBinaryKeyValues
{
    private const byte MapType = 0x00, StringType = 0x01, Int32Type = 0x02, EndMarker = 0x08;

    private static readonly UTF8Encoding Utf8 = new(false, throwOnInvalidBytes: true);

    public BinaryKvMap Read(ReadOnlySpan<byte> bytes)
    {
        var offset = 0;
        var children = ReadChildren(bytes, ref offset);
        if (offset != bytes.Length) throw new InvalidDataException($"Unexpected data after the end of the file at offset {offset}.");
        return new BinaryKvMap("", children);
    }

    public byte[] Write(BinaryKvMap root)
    {
        using var stream = new MemoryStream();
        WriteChildren(stream, root.Children);
        return stream.ToArray();
    }

    private static List<BinaryKv> ReadChildren(ReadOnlySpan<byte> bytes, ref int offset)
    {
        var children = new List<BinaryKv>();
        while (true)
        {
            if (offset >= bytes.Length) throw new InvalidDataException($"The data ends at offset {offset} before a map is closed.");
            var typeOffset = offset;
            var type = bytes[offset++];
            if (type == EndMarker) return children;

            var name = ReadCString(bytes, ref offset);
            switch (type)
            {
                case MapType:
                    children.Add(new BinaryKvMap(name, ReadChildren(bytes, ref offset)));
                    break;
                case StringType:
                    children.Add(new BinaryKvString(name, ReadCString(bytes, ref offset)));
                    break;
                case Int32Type:
                    if (bytes.Length - offset < 4) throw new InvalidDataException($"The data ends at offset {offset} inside the integer \"{name}\".");
                    children.Add(new BinaryKvInt32(name, BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..])));
                    offset += 4;
                    break;
                default:
                    throw new InvalidDataException($"Unknown value type 0x{type:X2} for \"{name}\" at offset {typeOffset}.");
            }
        }
    }

    private static string ReadCString(ReadOnlySpan<byte> bytes, ref int offset)
    {
        var length = bytes[offset..].IndexOf((byte)0);
        if (length < 0) throw new InvalidDataException($"A string starting at offset {offset} is not terminated.");
        string text;
        try { text = Utf8.GetString(bytes.Slice(offset, length)); }
        catch (DecoderFallbackException) { throw new InvalidDataException($"The string at offset {offset} is not valid UTF-8."); }
        offset += length + 1;
        return text;
    }

    private static void WriteChildren(Stream stream, List<BinaryKv> children)
    {
        foreach (var child in children)
        {
            switch (child)
            {
                case BinaryKvMap map:
                    stream.WriteByte(MapType);
                    WriteCString(stream, map.Name);
                    WriteChildren(stream, map.Children);
                    break;
                case BinaryKvString text:
                    stream.WriteByte(StringType);
                    WriteCString(stream, text.Name);
                    WriteCString(stream, text.Value);
                    break;
                case BinaryKvInt32 number:
                    stream.WriteByte(Int32Type);
                    WriteCString(stream, number.Name);
                    Span<byte> buffer = stackalloc byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(buffer, number.Value);
                    stream.Write(buffer);
                    break;
                default:
                    throw new NotSupportedException($"No binary form for {child.GetType().Name}.");
            }
        }
        stream.WriteByte(EndMarker);
    }

    private static void WriteCString(Stream stream, string text)
    {
        stream.Write(Utf8.GetBytes(text));
        stream.WriteByte(0);
    }
}
