using System.Buffers.Binary;
using System.Text;

namespace SilverModManager.Games.FromSoft.Formats;

/// <summary>
/// BND3, the binder container of the games that predate BND4.
/// </summary>
/// <remarks>
/// A 0x20-byte header (magic, version string, a format byte, a big-endian byte, a bit-order byte, entry count, the
/// offset where the entry headers and names end) is followed by the entry headers, the Shift_JIS names and the
/// 0x10-aligned data. The format byte decides which optional fields each entry header carries: a 64-bit data offset,
/// an id, a name offset, a second (uncompressed) size.
/// <para>
/// The format byte and each entry's flags byte are stored least significant bit first unless the bit-order byte at
/// 0x0E is set, so the flag values below are the decoded ones: a shipped archive's 0x74 is ids, names and the
/// uncompressed size.
/// </para>
/// <para>
/// Rebuilding is a structural clone, like <see cref="Bnd4"/>: header and names are copied verbatim and only the
/// sizes, offsets and member data are rewritten.
/// </para>
/// </remarks>
public sealed class Bnd3 : IBinder
{
    public static readonly Bnd3 Instance = new();

    private const int HeaderSize = 0x20;
    private const int DataAlign = 0x10;

    private const byte ForcesBigEndian = 0x01;
    private const byte HasIds = 0x02;
    // Two bits both mean names are present; archives set either or both.
    private const byte HasNames = 0x04 | 0x08;
    private const byte LongOffsets = 0x10;
    private const byte HasUncompressedSize = 0x20;

    // Entry flags bit marking a member whose stored bytes are compressed.
    private const byte MemberCompressed = 0x01;

    private static ReadOnlySpan<byte> Magic => "BND3"u8;

    private Bnd3() { }

    private readonly record struct Layout(int Count, byte Format, bool BigEndian, int Stride)
    {
        public bool Has(byte flag) => (Format & flag) != 0;

        public int OffsetWidth => Has(LongOffsets) ? 8 : 4;

        /// <summary>Where the uncompressed size sits in an entry header, after the offset, id and name offset.</summary>
        public int UncompressedSizeField => 8 + OffsetWidth + (Has(HasIds) ? 4 : 0) + (Has(HasNames) ? 4 : 0);
    }

    private readonly record struct Member(int Index, int Id, string Name, int Offset, int Size, byte Flags);

    /// <summary>
    /// Each entry's decoded flags byte in table order. Unlike <see cref="Read"/> this reports a compressed member
    /// rather than refusing it.
    /// </summary>
    public static IReadOnlyList<byte> EntryFlags(ReadOnlySpan<byte> data) =>
        Parse(data, allowCompressed: true).Members.Select(m => m.Flags).ToArray();

    /// <inheritdoc />
    public IReadOnlyList<BinderEntry> Read(ReadOnlySpan<byte> data)
    {
        var (_, members) = Parse(data);
        var entries = new BinderEntry[members.Count];
        for (var i = 0; i < entries.Length; i++)
        {
            var m = members[i];
            entries[i] = new BinderEntry(m.Id, m.Name, data.Slice(m.Offset, m.Size).ToArray());
        }

        return entries;
    }

    /// <inheritdoc />
    public byte[] Rebuild(ReadOnlySpan<byte> source, IReadOnlyDictionary<int, byte[]> replacements)
    {
        var (layout, members) = Parse(source);

        var known = members.Select(m => m.Id).ToHashSet();
        var unknown = replacements.Keys.Where(id => !known.Contains(id)).Order().ToArray();
        if (unknown.Length > 0)
            throw new Bnd3Exception($"BND3 has no entry with id {string.Join(", ", unknown)}; the archive layout changed, so this merge would silently do nothing");

        // The input already is the answer.
        if (replacements.Count == 0)
            return source.ToArray();

        // Data is laid out back to back in table order below, so a table whose offsets run in another order
        // would be silently reshuffled.
        for (var i = 1; i < members.Count; i++)
            if (members[i].Offset < members[i - 1].Offset)
                throw new Bnd3Exception($"BND3 entry {i} (id={members[i].Id}) is stored before entry {i - 1}; the data is not in table order");

        // Names and any bytes between them and the first member stay where they are.
        var firstOffset = members[0].Offset;
        var payloads = new byte[members.Count][];
        for (var i = 0; i < payloads.Length; i++)
            payloads[i] = replacements.GetValueOrDefault(members[i].Id) ?? source.Slice(members[i].Offset, members[i].Size).ToArray();
        var length = checked(firstOffset + payloads.Sum(p => (long)p.Length) + payloads[..^1].Sum(p => (long)AlignPad(p.Length)));
        if (length > int.MaxValue)
            throw new Bnd3Exception($"BND3 would be {length} bytes, past the 2 GiB the format can address");
        var output = new byte[length];
        source[..firstOffset].CopyTo(output);

        var cursor = firstOffset;
        for (var i = 0; i < payloads.Length; i++)
        {
            var payload = payloads[i];
            var entry = output.AsSpan(HeaderSize + i * layout.Stride);
            WriteInt32(entry[4..], payload.Length, layout.BigEndian);
            if (layout.Has(LongOffsets))
                WriteInt64(entry[8..], cursor, layout.BigEndian);
            else
                WriteInt32(entry[8..], cursor, layout.BigEndian);
            // Members are stored uncompressed (Parse refuses any other), so both sizes are the payload's length.
            if (layout.Has(HasUncompressedSize))
                WriteInt32(entry[layout.UncompressedSizeField..], payload.Length, layout.BigEndian);
            payload.CopyTo(output, cursor);
            cursor += payload.Length;
            // The last member gets no padding, since shipped archives carry none.
            if (i < payloads.Length - 1)
                cursor += AlignPad(payload.Length);
        }

        // The header's 0x14 marks where the names end, and they were copied in place, so it is already right.
        return output;
    }

    private static int AlignPad(int length) => -length & (DataAlign - 1);

    /// <summary>
    /// Reads the header and every entry header. These are arbitrary third-party archives, so every field is
    /// untrusted: a bad value must throw here, before a slice can run past the buffer.
    /// </summary>
    private static (Layout Layout, List<Member> Members) Parse(ReadOnlySpan<byte> data, bool allowCompressed = false)
    {
        if (data.Length < HeaderSize || !data[..Magic.Length].SequenceEqual(Magic))
            throw new Bnd3Exception("not a BND3 archive (bad magic)");

        var bitBigEndian = data[0x0E] != 0;
        var format = DecodeFormat(data[0x0C], bitBigEndian);
        var bigEndian = data[0x0D] != 0 || (format & ForcesBigEndian) != 0;
        var count = ReadInt32(data[0x10..], bigEndian);
        if (count < 0)
            throw new Bnd3Exception($"BND3 entry count is negative: {count}");

        var stride = 8
            + ((format & LongOffsets) != 0 ? 8 : 4)
            + ((format & HasIds) != 0 ? 4 : 0)
            + ((format & HasNames) != 0 ? 4 : 0)
            + ((format & HasUncompressedSize) != 0 ? 4 : 0);
        var layout = new Layout(count, format, bigEndian, stride);

        var headersEnd = HeaderSize + (long)count * stride;
        if (headersEnd > data.Length)
            throw new Bnd3Exception($"BND3 entry header table (count={count}, stride={stride}) doesn't fit the {data.Length}-byte buffer");

        var members = new List<Member>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = data[(HeaderSize + i * stride)..];
            var flags = DecodeBits(entry[0], bitBigEndian);
            var size = ReadInt32(entry[4..], bigEndian);
            long offset = layout.Has(LongOffsets) ? ReadInt64(entry[8..], bigEndian) : (uint)ReadInt32(entry[8..], bigEndian);
            var cursor = 8 + layout.OffsetWidth;
            var id = i;
            if (layout.Has(HasIds))
            {
                id = ReadInt32(entry[cursor..], bigEndian);
                cursor += 4;
            }

            var name = "";
            if (layout.Has(HasNames))
            {
                var nameOffset = ReadInt32(entry[cursor..], bigEndian);
                if (nameOffset < 0 || nameOffset >= data.Length)
                    throw new Bnd3Exception($"BND3 entry {i} (id={id}) name offset {nameOffset} is outside the {data.Length}-byte buffer");
                name = ReadName(data, nameOffset);
            }

            if (size < 0)
                throw new Bnd3Exception($"BND3 entry {i} (id={id}) has a negative size: {size}");
            // An offset inside the header table would hand the entry headers back as this entry's data.
            if (offset < headersEnd)
                throw new Bnd3Exception($"BND3 entry {i} (id={id}) data offset {offset} precedes the end of the entry headers at {headersEnd}");
            if (size > data.Length - offset)
                throw new Bnd3Exception($"BND3 entry {i} (id={id}) data (offset {offset}, size {size}) runs past the end of the {data.Length}-byte buffer");

            // Handing back the stored bytes would pass compressed data off as the member, and a rebuild would write
            // raw bytes into a member the game then tries to decompress. Nothing ships these yet, so refuse loudly.
            if (!allowCompressed && (flags & MemberCompressed) != 0)
                throw new Bnd3Exception($"BND3 entry {i} (id={id}) is stored compressed, which this reader does not decompress");

            members.Add(new Member(i, id, name, (int)offset, size, flags));
        }

        // Nothing above stops one entry's region from overlapping another's. Shipped archives lay entries out back
        // to back with only alignment padding, so a strict check costs real files nothing.
        var byOffset = members.OrderBy(m => m.Offset).ThenBy(m => m.Index).ToArray();
        for (var i = 1; i < byOffset.Length; i++)
        {
            var (prev, next) = (byOffset[i - 1], byOffset[i]);
            if (next.Offset < prev.Offset + prev.Size)
                throw new Bnd3Exception($"BND3 entry {prev.Index} (id={prev.Id}) data region [{prev.Offset}, {prev.Offset + prev.Size}) overlaps entry {next.Index} (id={next.Id}) starting at {next.Offset}");
        }

        return (layout, members);
    }

    private static byte DecodeFormat(byte raw, bool bitBigEndian)
    {
        // Some archives store the format in bit order without setting 0x0E. Read least significant bit first, such a
        // byte would set the top flag without the big-endian one, so it is taken as already in bit order.
        var inOrder = bitBigEndian || ((raw & 0x01) != 0 && (raw & 0x80) == 0);
        return inOrder ? raw : ReverseBits(raw);
    }

    private static byte DecodeBits(byte raw, bool bitBigEndian) => bitBigEndian ? raw : ReverseBits(raw);

    private static byte ReverseBits(byte b)
    {
        b = (byte)(((b & 0xF0) >> 4) | ((b & 0x0F) << 4));
        b = (byte)(((b & 0xCC) >> 2) | ((b & 0x33) << 2));
        return (byte)(((b & 0xAA) >> 1) | ((b & 0x55) << 1));
    }

    private static string ReadName(ReadOnlySpan<byte> data, int offset)
    {
        var end = data[offset..].IndexOf((byte)0);
        if (end < 0)
            throw new Bnd3Exception("unterminated string in BND3 entry name");
        try
        {
            return ShiftJis.Encoding.GetString(data.Slice(offset, end));
        }
        catch (DecoderFallbackException e)
        {
            throw new Bnd3Exception($"BND3 entry name at {offset} is not valid Shift_JIS: {e.Message}");
        }
    }

    private static int ReadInt32(ReadOnlySpan<byte> s, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s);

    private static long ReadInt64(ReadOnlySpan<byte> s, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadInt64BigEndian(s) : BinaryPrimitives.ReadInt64LittleEndian(s);

    private static void WriteInt32(Span<byte> s, int value, bool bigEndian)
    {
        if (bigEndian) BinaryPrimitives.WriteInt32BigEndian(s, value);
        else BinaryPrimitives.WriteInt32LittleEndian(s, value);
    }

    private static void WriteInt64(Span<byte> s, long value, bool bigEndian)
    {
        if (bigEndian) BinaryPrimitives.WriteInt64BigEndian(s, value);
        else BinaryPrimitives.WriteInt64LittleEndian(s, value);
    }
}
