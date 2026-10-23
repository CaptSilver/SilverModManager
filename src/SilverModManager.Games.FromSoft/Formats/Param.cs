using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace SilverModManager.Games.FromSoft.Formats;

/// <param name="NameOffset">Absolute offset into the source file's strings block; 0 means the row has no name.</param>
public sealed record ParamRow(int Id, byte[] Data, long NameOffset);

/// <param name="Header">The original 0x40 header, re-stamped on write.</param>
/// <param name="Strings">The strings block, copied verbatim.</param>
/// <param name="StringsOffset">Where <paramref name="Strings"/> sat in the source, to compute the shift on write.</param>
/// <param name="Tail">
/// The bytes between the last row and the strings block. In every shipped param that has any, they are the head of the
/// param type name, so they are carried through rather than synthesised.
/// </param>
public sealed record ParamTable(
    ImmutableArray<ParamRow> Rows,
    int Stride,
    byte[] Header,
    byte[] Strings,
    long StringsOffset,
    long ParamTypeOffset,
    byte[] Tail)
{
    /// <summary>The type name (e.g. <c>SP_EFFECT_PARAM_ST</c>), or empty when it is stored outside the strings block, which is kept but not decoded.</summary>
    public string ParamType
    {
        get
        {
            var relative = ParamTypeOffset - StringsOffset;
            if (relative < 0 || relative >= Strings.Length) return "";
            var span = Strings.AsSpan((int)relative);
            var end = span.IndexOf((byte)0);
            return Encoding.Latin1.GetString(end < 0 ? span : span[..end]);
        }
    }

    public ushort ParamdefDataVersion => BinaryPrimitives.ReadUInt16LittleEndian(Header.AsSpan(0x08));

    public ReadOnlySpan<byte> FormatFlags => Header.AsSpan(Param.FormatFlagsOffset, 4);
}

/// <summary>
/// PARAM: one game-data table, as found inside the regulation's binder. Rows are opaque blobs of one fixed width,
/// keyed by id, with no field layout. That is what lets a narrow transplant work without a catalogue of paramdefs:
/// moving a row between two files needs its bytes and the stride, nothing more.
/// </summary>
/// <remarks>
/// Layout (little-endian, 64-bit offsets), verified against Elden Ring's SpEffectParam:
/// <code>
/// 0x00  u32  strings offset
/// 0x08  u16  paramdef data version
/// 0x0A  u16  row count
/// 0x10  i64  param type offset
/// 0x2C  4B   format flags
/// 0x30  i64  row data offset
/// 0x40       row table, 24 bytes each: i32 id, u32 pad, i64 data offset, i64 name offset
///            row data, rowCount * stride bytes
///            strings block, to the end of the file
/// </code>
/// The reader implements this one layout, so <see cref="Read"/> refuses a file whose format flags say otherwise instead
/// of misparsing it. The flag bits follow SoulsFormats' PARAM header reader (SoulsFormats/Formats/PARAM/PARAM/PARAM.cs):
/// 0x2C is 0xFF for a big-endian file. 0x2D picks the offset width, 32-bit when both Flag01 (0x01) and IntDataOffset
/// (0x02) are set, else 64-bit when LongDataOffset (0x04) is, else a 16-bit data offset at 0x04; its OffsetParamType
/// bit (0x80) stores the type name by offset instead of inline at 0x10. 0x2E only says how row names are encoded, which
/// does not matter while names are carried as opaque offsets.
/// </remarks>
public static class Param
{
    public const int HeaderSize = 0x40;
    public const int RowEntrySize = 24;
    public const int FormatFlagsOffset = 0x2C;

    private const byte BigEndianMarker = 0xFF;
    private const byte Flag01 = 0x01;
    private const byte IntDataOffset = 0x02;
    private const byte LongDataOffset = 0x04;
    private const byte OffsetParamType = 0x80;

    public static ParamTable Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new ParamException($"PARAM header is truncated: {data.Length} bytes");
        RequireSupportedLayout(data.Slice(FormatFlagsOffset, 4));

        long stringsOffset = BinaryPrimitives.ReadUInt32LittleEndian(data);
        int rowCount = BinaryPrimitives.ReadUInt16LittleEndian(data[0x0A..]);
        var paramTypeOffset = BinaryPrimitives.ReadInt64LittleEndian(data[0x10..]);
        var dataOffset = BinaryPrimitives.ReadInt64LittleEndian(data[0x30..]);

        var tableEnd = HeaderSize + rowCount * (long)RowEntrySize;
        if (rowCount > 0 && dataOffset != tableEnd)
            throw new ParamException($"Row data starts at {dataOffset}, but the {rowCount}-entry row table ends at {tableEnd}: unexpected gap");
        if (!(0 < stringsOffset && stringsOffset <= data.Length) || tableEnd > data.Length)
            throw new ParamException($"PARAM offsets fall outside the file (strings offset {stringsOffset}, row table end {tableEnd}, size {data.Length})");

        var entries = new (int Id, long Offset, long NameOffset)[rowCount];
        for (var i = 0; i < rowCount; i++)
        {
            var entry = data[(HeaderSize + i * RowEntrySize)..];
            entries[i] = (BinaryPrimitives.ReadInt32LittleEndian(entry), BinaryPrimitives.ReadInt64LittleEndian(entry[8..]), BinaryPrimitives.ReadInt64LittleEndian(entry[16..]));
        }
        for (var i = 1; i < rowCount; i++)
        {
            if (entries[i].Offset < entries[i - 1].Offset)
                throw new ParamException("Row data offsets are not ascending in row-table order");
        }

        var stride = rowCount > 0 ? DeriveStride(entries.Select(e => e.Offset).ToArray(), stringsOffset) : 0;
        // Repeated offsets count as ascending, and a lone row starting past the strings block derives a negative width;
        // both would pass the overrun check below and hand every row back empty.
        if (rowCount > 0 && stride <= 0)
            throw new ParamException($"Derived row stride is {stride}: rows would read as empty");
        // Bounding it by the file keeps the offset arithmetic below from overflowing.
        if (stride > data.Length)
            throw new ParamException($"Derived row stride is {stride}, wider than the {data.Length}-byte file");

        var rows = ImmutableArray.CreateBuilder<ParamRow>(rowCount);
        foreach (var (id, offset, nameOffset) in entries)
        {
            // Zero is the "no name" convention shipped params use; anything else has to land inside the file.
            if (nameOffset != 0 && !(0 <= nameOffset && nameOffset <= data.Length))
                throw new ParamException($"Row {id} has a name offset ({nameOffset}) outside the file");
            // A negative offset is not an index from the end here, but one under the data offset would still splice the header in as row data.
            if (offset < dataOffset)
                throw new ParamException($"Row {id} data offset {offset} precedes the row-data section at {dataOffset}");
            if (offset > stringsOffset - stride)
                throw new ParamException($"Row {id} data overruns the strings block");
            rows.Add(new ParamRow(id, data.Slice((int)offset, (int)stride).ToArray(), nameOffset));
        }

        if (!(0 <= paramTypeOffset && paramTypeOffset <= data.Length))
            throw new ParamException($"Param type offset {paramTypeOffset} is outside the file");
        // Write re-packs the rows from the data offset, so rows that start later would move on a round trip.
        if (rowCount > 0 && entries[0].Offset != dataOffset)
            throw new ParamException($"The first row's data starts at {entries[0].Offset}, not at the row-data section ({dataOffset})");
        var tailStart = dataOffset + (long)rowCount * stride;
        if (!(0 <= tailStart && tailStart <= stringsOffset))
            throw new ParamException($"The row-data section at {dataOffset} does not fit before the strings block at {stringsOffset}");

        return new ParamTable(
            rows.MoveToImmutable(), (int)stride, data[..HeaderSize].ToArray(), data[(int)stringsOffset..].ToArray(),
            stringsOffset, paramTypeOffset, data[(int)tailStart..(int)stringsOffset].ToArray());
    }

    /// <summary>
    /// Whether two tables' derived strides can be compared as a layout fact. With one row there is no gap to measure,
    /// so the stride falls back to the distance to the strings block, which swallows whatever sits between the row and
    /// the strings (the head of the type name). How much of it does differs between the game's file and a tool's
    /// re-save, so comparing those widths reports a layout change that is not one; the paramdef data version is the
    /// signal that survives.
    /// </summary>
    public static bool StridesComparable(int rowsA, int rowsB) => rowsA > 1 && rowsB > 1;

    /// <summary>
    /// <paramref name="table"/> with a new row list, ordered by id. Ordering here rather than in <see cref="Write"/>
    /// keeps the invariant in one place: the game looks rows up by id, so an out-of-order table makes rows unreachable
    /// without corrupting anything a parser would notice. Write keeps whatever order it is given, so a param that
    /// ships out of order round-trips untouched.
    /// </summary>
    public static ParamTable ReplaceRows(ParamTable table, IEnumerable<ParamRow> rows)
        => table with { Rows = [..rows.OrderBy(r => r.Id)] };

    /// <summary>
    /// <paramref name="table"/> with rows overwritten and/or inserted, ids kept ascending. The two sets are separate
    /// because a transplant knows which rows it expects to exist and which to be new; getting that backwards is how a
    /// patch silently destroys an authored row or duplicates one. Shipped params can carry an id twice, which
    /// <see cref="Write"/> tolerates, but patching into one is ambiguous and is refused.
    /// </summary>
    public static ParamTable PatchRows(
        ParamTable table,
        IReadOnlyDictionary<int, byte[]>? overwrite = null,
        IReadOnlyDictionary<int, byte[]>? insert = null)
    {
        overwrite ??= new Dictionary<int, byte[]>();
        insert ??= new Dictionary<int, byte[]>();
        var existing = table.Rows.Select(r => r.Id).ToArray();
        var duplicated = existing.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).Order().Take(5).ToArray();
        if (duplicated.Length > 0)
            throw new ParamException($"{(table.ParamType.Length > 0 ? table.ParamType : "param")} has duplicate row ids [{string.Join(", ", duplicated)}]: refusing to patch a param where an id is ambiguous");
        if (!existing.SequenceEqual(existing.Order()))
            throw new ParamException("Row ids are not ascending: refusing to patch");

        var ids = existing.ToHashSet();
        var missing = overwrite.Keys.Where(k => !ids.Contains(k)).Order().ToArray();
        if (missing.Length > 0)
            throw new ParamException($"Expected to overwrite rows [{string.Join(", ", missing)}], but they are not in this param: the base file is not what the patch was built against");
        var clashing = insert.Keys.Where(ids.Contains).Order().ToArray();
        if (clashing.Length > 0)
            throw new ParamException($"Expected to insert rows [{string.Join(", ", clashing)}], but they already exist: the base file already carries them, so inserting would duplicate an id");

        var nameOffset = table.Rows.Length > 0 ? table.Rows[0].NameOffset : 0;
        var rows = table.Rows
            .Select(r => overwrite.TryGetValue(r.Id, out var data) ? r with { Data = data } : r)
            .Concat(insert.Select(kv => new ParamRow(kv.Key, kv.Value, nameOffset)));
        return ReplaceRows(table, rows);
    }

    /// <summary>Serialises <paramref name="table"/> back to bytes, in the row order it holds.</summary>
    public static byte[] Write(ParamTable table)
    {
        var count = table.Rows.Length;
        if (count > ushort.MaxValue)
            throw new ParamException($"A PARAM holds at most {ushort.MaxValue} rows; got {count}");
        var ragged = table.Rows.Select(r => r.Data.Length).Where(w => w != table.Stride).Distinct().Order().ToArray();
        if (ragged.Length > 0)
            throw new ParamException($"Every row must be exactly the param's stride ({table.Stride} bytes); got widths [{string.Join(", ", ragged)}]");

        var dataOffset = HeaderSize + count * (long)RowEntrySize;
        var stringsOffset = dataOffset + count * (long)table.Stride + table.Tail.Length;
        // String offsets are absolute, so moving the block moves them all together.
        var shift = stringsOffset - table.StringsOffset;
        if (shift != 0)
        {
            // An offset pointing before the block belongs to a region whose displacement was never measured, so relocating it would be a guess.
            var stray = table.Rows.Where(r => r.NameOffset != 0 && r.NameOffset < table.StringsOffset).Select(r => r.Id).Take(5).ToArray();
            if (stray.Length > 0)
                throw new ParamException($"Rows [{string.Join(", ", stray)}] name offsets point before the strings block, so they can't be relocated when the row count changes");
            if (table.ParamTypeOffset < table.StringsOffset)
                throw new ParamException("Param type string sits before the strings block, so it can't be relocated when the row count changes");
        }

        var output = new byte[stringsOffset + table.Strings.Length];
        table.Header.CopyTo(output, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(output, (uint)stringsOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(0x0A), (ushort)count);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(0x10), table.ParamTypeOffset + shift);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(0x30), dataOffset);

        for (var i = 0; i < count; i++)
        {
            var row = table.Rows[i];
            var rowOffset = dataOffset + i * (long)table.Stride;
            var entry = output.AsSpan(HeaderSize + i * RowEntrySize);
            BinaryPrimitives.WriteInt32LittleEndian(entry, row.Id);
            BinaryPrimitives.WriteInt64LittleEndian(entry[8..], rowOffset);
            // Zero is the "no name" sentinel, not an address; shifting it would point it at whatever now sits there.
            BinaryPrimitives.WriteInt64LittleEndian(entry[16..], row.NameOffset != 0 ? row.NameOffset + shift : 0);
            row.Data.CopyTo(output, rowOffset);
        }
        table.Tail.CopyTo(output, dataOffset + count * (long)table.Stride);
        table.Strings.CopyTo(output, stringsOffset);
        return output;
    }

    /// <summary>
    /// Row width from the gaps between consecutive row offsets. Deliberately not (strings offset - data offset) / rows:
    /// whatever sits between the last row and the strings block would be spread over every row and widen each one. A
    /// single row has no gap to measure, so there the distance to the strings block is the only option.
    /// </summary>
    private static long DeriveStride(long[] offsets, long stringsOffset)
    {
        if (offsets.Length == 1) return stringsOffset - offsets[0];
        var deltas = offsets.Zip(offsets.Skip(1), (a, b) => b - a).Distinct().Order().ToArray();
        if (deltas.Length != 1)
            throw new ParamException($"Row data is not fixed-stride: consecutive offsets differ by [{string.Join(", ", deltas.Take(5))}]; this param can't be row-patched safely");
        return deltas[0];
    }

    private static void RequireSupportedLayout(ReadOnlySpan<byte> flags)
    {
        var bigEndian = flags[0] == BigEndianMarker;
        var offsetsAre32Bit = (flags[1] & (Flag01 | IntDataOffset)) == (Flag01 | IntDataOffset);
        var offsetsAre64Bit = !offsetsAre32Bit && (flags[1] & LongDataOffset) != 0;
        var paramTypeByOffset = (flags[1] & OffsetParamType) != 0;
        if (!bigEndian && offsetsAre64Bit && paramTypeByOffset) return;

        var offsets = offsetsAre32Bit ? "32-bit data offsets"
            : offsetsAre64Bit ? "64-bit data offsets"
            : "a 16-bit data offset in the header";
        throw new ParamException(
            $"Unsupported PARAM layout, format flags {Convert.ToHexString(flags)}: {(bigEndian ? "big-endian" : "little-endian")}, {offsets}, " +
            $"param type {(paramTypeByOffset ? "stored by offset in the strings block" : "stored inline in the header")}. " +
            "Only little-endian files with 64-bit data offsets and the param type stored by offset are read.");
    }
}
