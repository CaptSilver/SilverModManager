using NexusMods.Games.FromSoft.Formats;

namespace NexusMods.Games.FromSoft.Tests.Formats.Fixtures;

/// <summary>Builds regulation-shaped files: a BND4 of raw member bodies under the game's ZSTD wrapper and AES layer.</summary>
public static class RegulationFixture
{
    public static readonly byte[] ZeroIv = new byte[16];

    /// <summary>The members the checked-in golden regulation was produced from.</summary>
    public static readonly (int Id, string Name, byte[] Data)[] GoldenEntries =
    [
        (1, "a.param", "AAAA"u8.ToArray()),
        (2, "b.param", Enumerable.Range(0, 40).Select(i => (byte)i).ToArray()),
        (3, "c.param", Enumerable.Repeat((byte)'C', 100).ToArray()),
    ];

    public const string GoldenVersion = "11701000";

    /// <summary>The decrypted, decompressed BND4 of a regulation.</summary>
    public static byte[] Payload(IReadOnlyList<(int Id, string Name, byte[] Data)> entries, string version)
        => Bnd4Fixture.Build(entries, version: System.Text.Encoding.ASCII.GetBytes(version));

    public static byte[] BuildFromEntries(IReadOnlyList<(int Id, string Name, byte[] Data)> entries, string version, FromSoftFormatTraits traits)
        => Regulation.Pack(Payload(entries, version), ZeroIv, traits);

    /// <summary>
    /// A regulation whose members are param tables, one per key of <paramref name="tables"/> (the binder entry id), named
    /// after their type and in ascending id order.
    /// </summary>
    public static byte[] Build(
        IReadOnlyDictionary<int, (string ParamType, IReadOnlyDictionary<int, byte[]> Rows, int Stride)> tables,
        string version,
        FromSoftFormatTraits traits)
    {
        var entries = tables.OrderBy(t => t.Key).Select(t =>
        {
            var (paramType, rows, stride) = t.Value;
            var blob = ParamFixture.Make(rows.Keys.Order().ToArray(), stride, paramType, fill: id => rows[id]);
            return (t.Key, $"{paramType}.param", blob);
        }).ToList();
        return BuildFromEntries(entries, version, traits);
    }
}
