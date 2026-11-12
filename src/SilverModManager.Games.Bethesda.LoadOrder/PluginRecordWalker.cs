// Follows esplugin src/group.rs (read_form_ids, read_records) and src/record.rs (record_header, read_record_id).
using System.Buffers.Binary;

namespace SilverModManager.Games.Bethesda.LoadOrder;

/// <summary>
/// Lists the FormIDs of every record in a TES4-format plugin. Only record and group headers are read: a record's
/// body is skipped by its size, which is the stored size for a compressed record too, so nothing is decompressed.
/// </summary>
public static class PluginRecordWalker
{
    /// <summary>
    /// The non-zero FormIDs of the records after the header record, in file order, raw: the high byte is the
    /// plugin's own master index, not yet resolved against its master list.
    /// </summary>
    /// <exception cref="ArgumentException">The format is Morrowind's, which has no FormIDs.</exception>
    /// <exception cref="InvalidPluginException">The stream is truncated or its groups do not nest as their sizes say.</exception>
    public static uint[] ReadFormIds(Stream stream, PluginFormat format, string fileName)
    {
        if (format == PluginFormat.Tes3)
            throw new ArgumentException("Morrowind plugins have no FormIDs; their records are identified by name", nameof(format));

        var headerLength = format == PluginFormat.Tes4Oblivion ? 20 : 24;
        var reader = new HeaderReader(stream, fileName, headerLength);

        // The header record's data is skipped like any record's; it carries masters, not records.
        var header = reader.ReadHeader();
        if (!header.IsType("TES4"u8))
            throw new InvalidPluginException($"{fileName}: expected a TES4 header record but found '{header.PrintableType}'");

        reader.Skip(header.Size);

        var formIds = new List<uint>();
        while (reader.HasMore())
        {
            var group = reader.ReadHeader();
            if (!group.IsType("GRUP"u8))
                throw new InvalidPluginException($"{fileName}: expected a group at the top level but found '{group.PrintableType}'");

            ReadGroupContents(reader, group.GroupContentSize(headerLength, fileName), formIds, fileName);
        }

        return [.. formIds];
    }

    private static void ReadGroupContents(HeaderReader reader, uint contentSize, List<uint> formIds, string fileName)
    {
        // Sizes are checked in 64 bits: a group can claim more than the file holds.
        long consumed = 0;
        while (consumed < contentSize)
        {
            var header = reader.ReadHeader();
            consumed += reader.HeaderLength;

            if (header.IsType("GRUP"u8))
            {
                var inner = header.GroupContentSize(reader.HeaderLength, fileName);
                consumed += inner;
                if (consumed > contentSize)
                    throw new InvalidPluginException($"{fileName}: a group runs past the end of the group that holds it");

                ReadGroupContents(reader, inner, formIds, fileName);
            }
            else
            {
                consumed += header.Size;
                if (consumed > contentSize)
                    throw new InvalidPluginException($"{fileName}: a record runs past the end of the group that holds it");

                reader.Skip(header.Size);
                if (header.FormId != 0) formIds.Add(header.FormId);
            }
        }
    }

    private readonly record struct RecordHeader(byte[] Bytes)
    {
        public uint Size => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(4));

        // Records and groups share the first 8 bytes; a record's FormID follows its flags.
        public uint FormId => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(12));

        public bool IsType(ReadOnlySpan<byte> type) => Bytes.AsSpan(0, 4).SequenceEqual(type);

        public string PrintableType => new([.. Bytes.AsSpan(0, 4).ToArray().Select(b => b is >= 0x20 and < 0x7f ? (char)b : '?')]);

        /// <summary>A group's size counts its own header; what is left is the records and groups inside it.</summary>
        public uint GroupContentSize(int headerLength, string fileName) =>
            Size >= headerLength
                ? Size - (uint)headerLength
                : throw new InvalidPluginException($"{fileName}: a group claims {Size} bytes, fewer than its own {headerLength}-byte header");
    }

    /// <summary>Reads headers and skips bodies without needing the stream to seek.</summary>
    private sealed class HeaderReader(Stream stream, string fileName, int headerLength)
    {
        private readonly byte[] _scratch = new byte[8192];
        private int _peeked = -1;

        public int HeaderLength { get; } = headerLength;

        public bool HasMore()
        {
            if (_peeked >= 0) return true;

            _peeked = stream.ReadByte();
            return _peeked >= 0;
        }

        public RecordHeader ReadHeader()
        {
            var bytes = new byte[HeaderLength];
            var start = 0;
            if (_peeked >= 0)
            {
                bytes[0] = (byte)_peeked;
                _peeked = -1;
                start = 1;
            }

            ReadExactly(bytes.AsSpan(start), "a record header");
            return new RecordHeader(bytes);
        }

        public void Skip(uint count)
        {
            if (stream.CanSeek)
            {
                // Seeking past the end is allowed by streams, so the length is checked first.
                if (count > stream.Length - stream.Position)
                    throw new InvalidPluginException($"{fileName}: the file ends inside a record");

                stream.Seek(count, SeekOrigin.Current);
                return;
            }

            var left = count;
            while (left > 0)
            {
                var chunk = (int)Math.Min(left, (uint)_scratch.Length);
                ReadExactly(_scratch.AsSpan(0, chunk), "a record body");
                left -= (uint)chunk;
            }
        }

        private void ReadExactly(Span<byte> into, string what)
        {
            try
            {
                stream.ReadExactly(into);
            }
            catch (EndOfStreamException)
            {
                throw new InvalidPluginException($"{fileName}: the file ends inside {what}");
            }
        }
    }
}
