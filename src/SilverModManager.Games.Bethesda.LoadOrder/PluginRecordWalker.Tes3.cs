// Follows esplugin src/record_id.rs (NamespacedId, Namespace) and src/record.rs (parse_morrowind_record_id,
// record_id_subrecord_types and the CELL, PGRD and SCPT id mappers).
using System.IO.Hashing;

namespace SilverModManager.Games.Bethesda.LoadOrder;

public static partial class PluginRecordWalker
{
    private const int Tes3HeaderLength = 16;

    /// <summary>Reads the records of a plugin of any format.</summary>
    /// <exception cref="InvalidPluginException">See <see cref="ReadFormIds"/> and <see cref="ReadNamedIds"/>.</exception>
    public static PluginRecords Read(Stream stream, PluginFormat format, string fileName) =>
        format == PluginFormat.Tes3
            ? new PluginRecords([], ReadNamedIds(stream, fileName))
            : new PluginRecords(ReadFormIds(stream, format, fileName), []);

    /// <summary>
    /// Lists the id of every record of a Morrowind plugin that has one, ascending. Morrowind has no FormIDs; a
    /// record is named by a subrecord that depends on its type, so a record's body is read, not skipped, when its
    /// type is one that has an id.
    /// </summary>
    /// <exception cref="InvalidPluginException">The stream is truncated, does not start with a TES3 record, or a record's subrecords do not fit in it.</exception>
    public static ulong[] ReadNamedIds(Stream stream, string fileName)
    {
        var reader = new HeaderReader(stream, fileName, Tes3HeaderLength);

        var header = reader.ReadHeader();
        if (!header.IsType("TES3"u8))
            throw new InvalidPluginException($"{fileName}: expected a TES3 header record but found '{header.PrintableType}'");

        reader.Skip(header.Size);

        var ids = new List<ulong>();
        while (reader.HasMore())
        {
            var record = reader.ReadHeader();
            var wanted = IdSubrecordTypes(record.Bytes.AsSpan(0, 4));
            if (wanted.Length == 0)
            {
                reader.Skip(record.Size);
                continue;
            }

            var body = reader.ReadBody(record.Size);
            if (IdData(record.Bytes.AsSpan(0, 4), body, wanted, fileName) is { } data)
                ids.Add(NamedId(record.Bytes.AsSpan(0, 4), data.Span));
        }

        ids.Sort();
        return [.. ids];
    }

    /// <summary>
    /// The id esplugin gives a record of the given type whose id data is <paramref name="idData"/>. Ids are equal
    /// when the data is equal and the types share a namespace, as most do; a few types have a namespace to themselves.
    /// </summary>
    public static ulong NamedId(string recordType, ReadOnlySpan<byte> idData)
    {
        Span<byte> type = stackalloc byte[4];
        for (var i = 0; i < 4; i++) type[i] = (byte)recordType[i];

        return NamedId(type, idData);
    }

    private static ulong NamedId(ReadOnlySpan<byte> recordType, ReadOnlySpan<byte> idData) =>
        XxHash3.HashToUInt64(idData, seed: Namespace(recordType));

    // The namespaces of esplugin's Namespace enum; every type not listed is "other".
    private static int Namespace(ReadOnlySpan<byte> type) => type switch
    {
        _ when type.SequenceEqual("RACE"u8) => 0,
        _ when type.SequenceEqual("CLAS"u8) => 1,
        _ when type.SequenceEqual("BSGN"u8) => 2,
        _ when type.SequenceEqual("SCPT"u8) => 3,
        _ when type.SequenceEqual("CELL"u8) => 4,
        _ when type.SequenceEqual("FACT"u8) => 5,
        _ when type.SequenceEqual("SOUN"u8) => 6,
        _ when type.SequenceEqual("GLOB"u8) => 7,
        _ when type.SequenceEqual("REGN"u8) => 8,
        _ when type.SequenceEqual("SKIL"u8) => 9,
        _ when type.SequenceEqual("MGEF"u8) => 10,
        _ when type.SequenceEqual("LAND"u8) => 11,
        _ when type.SequenceEqual("PGRD"u8) => 12,
        _ when type.SequenceEqual("DIAL"u8) => 13,
        _ => 14,
    };

    private static readonly string[] NameTypes =
    [
        "GMST", "GLOB", "CLAS", "FACT", "RACE", "SOUN", "REGN", "BSGN", "LTEX", "STAT", "DOOR", "MISC", "WEAP", "CONT",
        "SPEL", "CREA", "BODY", "LIGH", "ENCH", "NPC_", "ARMO", "CLOT", "REPA", "ACTI", "APPA", "LOCK", "PROB", "INGR",
        "BOOK", "ALCH", "LEVI", "LEVC", "SNDG", "DIAL",
    ];

    /// <summary>The subrecords that make up a record type's id, or none when the type has no id.</summary>
    private static string[] IdSubrecordTypes(ReadOnlySpan<byte> recordType)
    {
        var type = System.Text.Encoding.Latin1.GetString(recordType);
        return type switch
        {
            "SKIL" or "MGEF" => ["INDX"],
            // identical INFO records can sit under different DIAL records and still be different records; esplugin ignores that too.
            "INFO" => ["INAM"],
            "LAND" => ["INTV"],
            "SCPT" => ["SCHD"],
            "CELL" or "PGRD" => ["DATA", "NAME"],
            _ => NameTypes.Contains(type) ? ["NAME"] : [],
        };
    }

    private static readonly ReadOnlyMemory<byte>? NoId = null;

    /// <summary>The bytes that identify the record, or null when it has no id.</summary>
    private static ReadOnlyMemory<byte>? IdData(ReadOnlySpan<byte> recordType, byte[] body, string[] wanted, string fileName)
    {
        // Subrecords are taken in file order, the first of each wanted type, until every wanted type is found.
        var found = new List<(string Type, ReadOnlyMemory<byte> Data)>(wanted.Length);
        var left = new List<string>(wanted);
        var position = 0;
        while (position < body.Length && left.Count > 0)
        {
            if (body.Length - position < 8)
                throw new InvalidPluginException($"{fileName}: a subrecord header runs past the end of its record");

            var length = BitConverter.ToUInt32(body, position + 4);
            if (length > body.Length - position - 8)
                throw new InvalidPluginException($"{fileName}: a subrecord runs past the end of its record");

            var type = System.Text.Encoding.Latin1.GetString(body, position, 4);
            if (left.Remove(type)) found.Add((type, body.AsMemory(position + 8, (int)length)));
            position += 8 + (int)length;
        }

        switch (System.Text.Encoding.Latin1.GetString(recordType))
        {
            case "CELL":
                // The name comes before the data in the file, but which of them is the id depends on the data.
                if (found is not [{ Type: "NAME" } name, { Type: "DATA" } cellData]) return null;

                // An exterior cell (the interior flag is clear) is its grid position, an interior one its name.
                if (cellData.Data.Length == 12 && (cellData.Data.Span[0] & 1) == 0) return cellData.Data[4..];
                // A null literal converts to an empty array and so to empty memory, which is not the same as no id.
                return name.Data.Length > 1 ? name.Data : NoId;

            case "PGRD":
                if (found is not [{ Type: "DATA" } gridData, { Type: "NAME" } gridName]) return null;

                // A path grid with no grid position is named instead.
                if (gridData.Data.Length < 8) return gridName.Data;
                var grid = gridData.Data[..8];
                return grid.Span.IndexOfAnyExcept((byte)0) < 0 ? gridName.Data : grid;

            case "SCPT":
                return found.Count > 0 && found[0].Data.Length >= 32 ? found[0].Data[..32] : NoId;

            default:
                return found.Count > 0 ? found[0].Data : NoId;
        }
    }
}
