using System.Buffers.Binary;

namespace SilverModManager.Games.FromSoft.Formats;

/// <summary>
/// BND4, the archive container behind a game's message, behaviour and param binders.
/// </summary>
/// <remarks>
/// Rebuilding is a structural clone, not a fresh serialization. Only member contents ever change, never which
/// members exist, so everything from the entry headers' fixed fields to the start of the data section is copied
/// verbatim. That includes the hash-bucket table at 0x38, which a from-scratch writer could only reproduce by
/// reimplementing FromSoft's hash.
/// </remarks>
public sealed class Bnd4 : IBinder
{
    public static readonly Bnd4 Instance = new();

    private const int HeaderSize = 0x40;
    private const int DataAlign = 0x10;

    // Size, size, offset, id and name offset: the fields read out of every entry header.
    private const int EntryFieldsSize = 0x24;

    private static ReadOnlySpan<byte> Magic => "BND4"u8;

    private Bnd4() { }

    /// <inheritdoc />
    public IReadOnlyList<BinderEntry> Read(ReadOnlySpan<byte> data)
    {
        var (count, entryHeaderSize, dataOffset) = EntryLayout(data);
        var entries = new List<BinderEntry>(count);
        var spans = new List<(uint Offset, long Size, int Index, int Id)>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = data[(HeaderSize + i * (int)entryHeaderSize)..];
            var size = BinaryPrimitives.ReadInt64LittleEndian(entry[0x10..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[0x18..]);
            var id = BinaryPrimitives.ReadInt32LittleEndian(entry[0x1C..]);
            var nameOffset = BinaryPrimitives.ReadInt32LittleEndian(entry[0x20..]);

            if (size < 0)
                throw new Bnd4Exception($"BND4 entry {i} (id={id}) has a negative size: {size}");
            // An offset before the data section would slice the header, names or hash table and hand them back
            // as this entry's data.
            if (offset < dataOffset)
                throw new Bnd4Exception($"BND4 entry {i} (id={id}) data offset {offset} precedes the declared data section at {dataOffset}");
            if (size > data.Length - (long)offset)
                throw new Bnd4Exception($"BND4 entry {i} (id={id}) data (offset {offset}, size {size}) runs past the end of the {data.Length}-byte buffer");
            if (nameOffset < 0 || nameOffset >= data.Length)
                throw new Bnd4Exception($"BND4 entry {i} (id={id}) name offset {nameOffset} is outside the {data.Length}-byte buffer");

            var name = Utf16Strings.ReadZ(data, nameOffset, m => new Bnd4Exception($"{m} in BND4 entry name"));
            entries.Add(new BinderEntry(id, name, data.Slice((int)offset, (int)size).ToArray()));
            spans.Add((offset, size, i, id));
        }

        // Nothing above stops one entry's region from overlapping another's, and an overlap means at least one
        // of them is not the bytes its own header claims. Shipped archives lay entries out back to back with only
        // alignment padding, so a strict check costs real files nothing.
        spans.Sort();
        for (var i = 1; i < spans.Count; i++)
        {
            var (offset, size, index, id) = spans[i - 1];
            var next = spans[i];
            if (next.Offset < offset + size)
                throw new Bnd4Exception($"BND4 entry {index} (id={id}) data region [{offset}, {offset + size}) overlaps entry {next.Index} (id={next.Id}) starting at {next.Offset}");
        }

        return entries;
    }

    /// <inheritdoc />
    public byte[] Rebuild(ReadOnlySpan<byte> source, IReadOnlyDictionary<int, byte[]> replacements)
    {
        var (_, entryHeaderSize, _) = EntryLayout(source);
        var entries = Read(source);

        var known = entries.Select(e => e.Id).ToHashSet();
        var unknown = replacements.Keys.Where(id => !known.Contains(id)).Order().ToArray();
        if (unknown.Length > 0)
            throw new Bnd4Exception($"BND4 has no entry with id {string.Join(", ", unknown)}; the archive layout changed, so this merge would silently do nothing");

        // The input already is the answer. Recomputing it would risk drifting from the original when the archive
        // has quirks (see the gap below) for a rebuild that has nothing to do.
        if (replacements.Count == 0)
            return source.ToArray();

        // Replacements are non-empty and all known, so at least one entry exists.
        var firstOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(source[(HeaderSize + 0x18)..]);

        var payloads = entries.Select(e => replacements.GetValueOrDefault(e.Id, e.Data)).ToArray();
        var length = checked(firstOffset + payloads.Sum(p => p.Length) + Padding(payloads));
        var output = new byte[length];

        // The header's data offset marks the end of the hash table, not necessarily an aligned entry start:
        // shipped archives can leave a few bytes between the two. Anchor on the first entry's own offset and
        // carry that gap across verbatim.
        source[..firstOffset].CopyTo(output);

        var cursor = firstOffset;
        for (var i = 0; i < payloads.Length; i++)
        {
            var payload = payloads[i];
            var header = output.AsSpan(HeaderSize + i * (int)entryHeaderSize);
            BinaryPrimitives.WriteInt64LittleEndian(header[0x08..], payload.Length);
            BinaryPrimitives.WriteInt64LittleEndian(header[0x10..], payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x18..], (uint)cursor);
            payload.CopyTo(output, cursor);
            cursor += payload.Length;
            // Padding aligns the next entry; the last one gets none, since shipped archives carry none and it
            // would grow the file past its original length.
            if (i < payloads.Length - 1)
                cursor += AlignPad(payload.Length);
        }

        return output;
    }

    private static int AlignPad(int length) => -length & (DataAlign - 1);

    private static int Padding(byte[][] payloads) => payloads[..^1].Sum(p => AlignPad(p.Length));

    /// <summary>
    /// The entry count, entry header size and data offset from the header. These are arbitrary third-party mod
    /// archives, so every field is untrusted: a bad value must throw here, before a loop can read past the buffer
    /// or spin on a bogus count.
    /// </summary>
    private static (int Count, long EntryHeaderSize, long DataOffset) EntryLayout(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || !data[..Magic.Length].SequenceEqual(Magic))
            throw new Bnd4Exception("not a BND4 archive (bad magic)");

        var count = BinaryPrimitives.ReadInt32LittleEndian(data[0x0C..]);
        var entryHeaderSize = BinaryPrimitives.ReadInt64LittleEndian(data[0x20..]);
        var dataOffset = BinaryPrimitives.ReadInt64LittleEndian(data[0x28..]);

        if (count < 0)
            throw new Bnd4Exception($"BND4 entry count is negative: {count}");
        // A header size under the fields every entry holds would let entries overlap, or, at 0, let an absurd
        // count pass the table-fits-the-file check below and spin the read loop. Checking it here also means
        // the per-entry loop never has to re-check the bound.
        if (count > 0 && entryHeaderSize < EntryFieldsSize)
            throw new Bnd4Exception($"BND4 entry header size {entryHeaderSize} is smaller than the {EntryFieldsSize} bytes of fields every entry header must hold");
        // A size past the file cannot fit; bailing here also keeps the product below from overflowing.
        if (count > 0 && entryHeaderSize > data.Length)
            throw new Bnd4Exception($"BND4 entry header table (count={count}, header_size={entryHeaderSize}) doesn't fit the {data.Length}-byte buffer");

        var headersEnd = HeaderSize + count * entryHeaderSize;
        if (headersEnd > data.Length)
            throw new Bnd4Exception($"BND4 entry header table (count={count}, header_size={entryHeaderSize}) doesn't fit the {data.Length}-byte buffer");
        if (dataOffset < headersEnd || dataOffset > data.Length)
            throw new Bnd4Exception($"BND4 data section (offset {dataOffset}) overlaps the entry headers or exceeds the {data.Length}-byte buffer");

        return (count, entryHeaderSize, dataOffset);
    }
}
