using System.Buffers.Binary;
using System.Text;

namespace NexusMods.Games.FromSoft.Tests.Formats.Fixtures;

/// <summary>
/// Builds PARAM tables in the layout Elden Ring ships: a 0x40 header, a 24-byte row table, fixed-stride row data and
/// then one strings block. <c>tail</c> fills the region between the last row and the strings block, which some shipped
/// params use to hold the head of their type name.
/// </summary>
public static class ParamFixture
{
    public const int HeaderSize = 0x40;
    public const int RowEntrySize = 24;

    /// <summary>The format flags (0x2C-0x2F) of the params the layout was verified against.</summary>
    public static readonly byte[] DefaultFormatFlags = [0x00, 0x85, 0x07, 0x00];

    public static byte[] Make(
        IReadOnlyList<int> ids,
        int stride = 8,
        string paramType = "TEST_PARAM_ST",
        Func<int, byte[]>? fill = null,
        byte[]? tail = null,
        byte[]? formatFlags = null)
    {
        tail ??= [];
        formatFlags ??= DefaultFormatFlags;
        var n = ids.Count;
        var dataOffset = HeaderSize + n * RowEntrySize;
        var stringsOffset = dataOffset + n * stride + tail.Length;
        var typeBytes = Encoding.ASCII.GetBytes(paramType);
        // Every row names itself with the terminator of the type string: row names are an editor convenience the game never stores.
        var nameOffset = stringsOffset + typeBytes.Length;

        var output = new byte[stringsOffset + typeBytes.Length + 5];
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x00), (uint)stringsOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(0x06), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(0x08), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(0x0A), (ushort)n);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(0x10), stringsOffset);
        formatFlags.CopyTo(output, 0x2C);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(0x30), dataOffset);

        for (var i = 0; i < n; i++)
        {
            var entry = output.AsSpan(HeaderSize + i * RowEntrySize);
            BinaryPrimitives.WriteInt32LittleEndian(entry, ids[i]);
            BinaryPrimitives.WriteInt64LittleEndian(entry[8..], dataOffset + i * stride);
            BinaryPrimitives.WriteInt64LittleEndian(entry[16..], nameOffset);
            var row = fill is null ? Enumerable.Repeat((byte)(ids[i] & 0xFF), stride).ToArray() : fill(ids[i]);
            row.CopyTo(output, dataOffset + i * stride);
        }
        tail.CopyTo(output, dataOffset + n * stride);
        typeBytes.CopyTo(output, stringsOffset);
        return output;
    }
}
