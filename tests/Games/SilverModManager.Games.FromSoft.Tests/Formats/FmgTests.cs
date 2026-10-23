using System.Buffers.Binary;
using FluentAssertions;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Tests.Formats.Fixtures;

namespace NexusMods.Games.FromSoft.Tests.Formats;

public class FmgTests
{
    private const int HeaderSize = 0x28;

    private static byte[] Written(params (int Id, string? Text)[] entries) =>
        Fmg.Write(entries.ToDictionary(e => e.Id, e => e.Text));

    [Fact]
    public void RoundTripPreservesEntries()
    {
        var entries = new Dictionary<int, string?> { [100] = "Resurrect", [101] = "Teleport", [102] = null, [500] = "Boss Resurrection" };
        Fmg.Read(Fmg.Write(entries)).Should().Equal(entries);
    }

    [Fact]
    public void RoundTripPreservesNonAscii()
    {
        // Game text is UTF-16 and full of accents and CJK; a one-byte assumption mangles item names.
        var entries = new Dictionary<int, string?> { [1] = "Swordhand of Night Jolán", [2] = "月光の大剣" };
        Fmg.Read(Fmg.Write(entries)).Should().Equal(entries);
    }

    [Fact]
    public void SparseIdsSurviveARoundTrip()
    {
        var entries = new Dictionary<int, string?> { [1] = "a", [2] = "b", [3] = "c", [9000] = "far", [9001] = "apart", [70000] = "z" };
        Fmg.Read(Fmg.Write(entries)).Should().Equal(entries);
    }

    [Fact]
    public void NegativeIdsSurviveARoundTrip()
    {
        var entries = new Dictionary<int, string?> { [-2] = "a", [-1] = "b", [0] = "c", [int.MinValue] = "min", [int.MaxValue] = "max" };
        Fmg.Read(Fmg.Write(entries)).Should().Equal(entries);
    }

    [Fact]
    public void AnEmptyTableRoundTrips()
    {
        Fmg.Read(Fmg.Write(new Dictionary<int, string?>())).Should().BeEmpty();
    }

    [Fact]
    public void ConsecutiveIdsShareOneRange()
    {
        var blob = Written((1, "a"), (2, "b"), (3, "c"), (10, "d"));
        BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(0x0C)).Should().Be(2, "ids 1-3 are one run and 10 another");
    }

    [Fact]
    public void DuplicateStringsAreShared()
    {
        // Vanilla files share repeated strings, and it keeps the merged file from growing.
        var entries = Enumerable.Range(0, 50).ToDictionary(i => i, _ => (string?)"same text");
        var blob = Fmg.Write(entries);
        Fmg.Read(blob).Should().Equal(entries);
        CountOf(blob, "same text"u8.ToArray().SelectMany(b => new byte[] { b, 0 }).Append((byte)0).Append((byte)0).ToArray()).Should().Be(1);
    }

    [Fact]
    public void HeaderFieldsAreWrittenTheWayTheGameExpects()
    {
        var blob = Written((1, "a"));
        blob[2].Should().Be(2, "version 2 has 64-bit string offsets");
        BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(4)).Should().Be(blob.Length);
        BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(0x08)).Should().Be(1);
        BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(0x14)).Should().Be(0xFF);
    }

    [Fact]
    public void WriteMatchesTheBytesErmWrites()
    {
        // Includes a null entry in the middle of a run and a string used twice.
        var entries = new Dictionary<int, string?> { [1] = "a", [2] = "b", [10] = null, [11] = "a" };
        Fmg.Write(entries).Should().Equal(GoldenFilesTests.Read("fmg_synthetic.fmg"));
    }

    [Fact]
    public void ReadsTheGoldenTable()
    {
        Fmg.Read(GoldenFilesTests.Read("fmg_synthetic.fmg")).Should().Equal(
            new Dictionary<int, string?> { [1] = "a", [2] = "b", [10] = null, [11] = "a" });
    }

    [Fact]
    public void ReadRejectsAnUnsupportedVersion()
    {
        var blob = Written((1, "x"));
        blob[2] = 1;
        var act = () => Fmg.Read(blob);
        act.Should().Throw<FmgException>().WithMessage("*version*");
    }

    [Fact]
    public void ReadRejectsARangePointingPastTheOffsetTable()
    {
        var blob = Written((1, "a"), (2, "b"));
        blob[0x10] = (byte)Math.Max(0, blob[0x10] - 1);
        var act = () => Fmg.Read(blob);
        act.Should().Throw<FmgException>().WithMessage("*points past the offset table*");
    }

    [Fact]
    public void ReadRejectsARangeThatSpansTheWholeIdSpace()
    {
        // A first..last walk in 32-bit arithmetic would wrap past int.MaxValue and never end.
        var blob = Written((1, "a"), (2, "b"));
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(HeaderSize + 4), int.MinValue);
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(HeaderSize + 8), int.MaxValue);
        var act = () => Fmg.Read(blob);
        act.Should().Throw<FmgException>().WithMessage("*points past the offset table*");
    }

    [Fact]
    public void ReadRejectsAReversedRange()
    {
        var blob = Written((1, "a"), (2, "b"));
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(HeaderSize + 4), 5);
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(HeaderSize + 8), 1);
        var act = () => Fmg.Read(blob);
        act.Should().Throw<FmgException>().WithMessage("*reversed*");
    }

    [Fact]
    public void ReadRejectsANegativeStringOffset()
    {
        var blob = Written((1, "ab"), (2, "cd"));
        var offsetsOff = (int)BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(0x18));
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(offsetsOff), -10);
        var act = () => Fmg.Read(blob);
        act.Should().Throw<FmgException>().WithMessage("*negative*");
    }

    [Fact]
    public void ReadRejectsAStringOffsetPastTheEndOfTheBuffer()
    {
        var blob = Written((1, "a"));
        var offsetsOff = (int)BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(0x18));
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(offsetsOff), blob.Length + 1000);
        var act = () => Fmg.Read(blob);
        act.Should().Throw<FmgException>().WithMessage("*past the end*");
    }

    [Theory]
    [InlineData("past the end")]
    [InlineData("negative")]
    [InlineData("long.MaxValue")]
    public void ReadRejectsACorruptOffsetsTableOffset(string corruption)
    {
        var blob = Written((1, "a"), (2, "b"));
        var offset = corruption switch
        {
            "past the end" => blob.Length + 1000L,
            "negative" => -1L,
            _ => long.MaxValue,
        };
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(0x18), offset);
        var act = () => Fmg.Read(blob);
        act.Should().Throw<FmgException>().WithMessage("*offset table*");
    }

    [Fact]
    public void ReadRejectsANegativeStringCount()
    {
        var blob = Written((1, "a"));
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(0x10), -1);
        var act = () => Fmg.Read(blob);
        act.Should().Throw<FmgException>().WithMessage("*offset table*runs outside*");
    }

    [Fact]
    public void ReadRejectsARangeStartingBeforeTheOffsetTable()
    {
        var blob = Written((1, "a"), (2, "b"));
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(HeaderSize), -1);
        var act = () => Fmg.Read(blob);
        act.Should().Throw<FmgException>().WithMessage("*points past the offset table*");
    }

    [Fact]
    public void ReadRejectsANegativeRangeCount()
    {
        var blob = Written((1, "a"), (2, "b"));
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(0x0C), -1);
        var act = () => Fmg.Read(blob);
        act.Should().Throw<FmgException>().WithMessage("*negative*");
    }

    [Fact]
    public void ReadRejectsARangeCountThatOverrunsTheRangeTable()
    {
        var blob = Written((1, "a"), (2, "b"));
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(0x0C), 1000);
        var act = () => Fmg.Read(blob);
        act.Should().Throw<FmgException>().WithMessage("*range table*");
    }

    [Fact]
    public void ReadRejectsDataShorterThanTheHeader()
    {
        var act = () => Fmg.Read(new byte[10]);
        act.Should().Throw<FmgException>().WithMessage("*shorter than its header*");
    }

    [Fact]
    public void ReadRejectsAnUnterminatedString()
    {
        var blob = Written((1, "a"));
        var act = () => Fmg.Read(blob.AsSpan(0, blob.Length - 2));
        act.Should().Throw<FmgException>().WithMessage("*unterminated*");
    }

    [Fact]
    public void WriteRejectsTextThatIsNotValidUtf16()
    {
        // A lone surrogate has no UTF-16 encoding; writing a replacement character would change the text silently.
        var act = () => Written((7, "broken \uD800 text"));
        act.Should().Throw<FmgException>().WithMessage("*id 7*UTF-16*");
    }

    private static int CountOf(byte[] haystack, byte[] needle)
    {
        var count = 0;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) count++;
        return count;
    }
}

public class MsgbndFixtureTests
{
    private static readonly Dictionary<int, IReadOnlyDictionary<int, string?>> GoldenTables = new()
    {
        [1] = new Dictionary<int, string?> { [1] = "a", [2] = "b", [10] = null, [11] = "a" },
        [2] = new Dictionary<int, string?> { [20] = "x", [21] = "y" },
    };

    private static FromSoftFormatTraits Traits => NexusMods.Games.FromSoft.EldenRing.EldenRingGame.Definition.Formats;

    [Fact]
    public void TheUncompressedBinderMatchesTheOneErmBuilds()
    {
        MsgbndFixture.BuildUncompressed(GoldenTables, Traits).Should().Equal(GoldenFilesTests.Read("msgbnd_synthetic.bnd"));
    }

    [Fact]
    public void TheBinderErmBuiltReadsBackAsItsTables()
    {
        var tables = Binders.For(Traits).Read(GoldenFilesTests.Read("msgbnd_synthetic.bnd"))
            .ToDictionary(e => e.Id, e => Fmg.Read(e.Data));
        tables.Keys.Should().Equal(1, 2);
        foreach (var (id, table) in GoldenTables)
            tables[id].Should().Equal(table);
    }
}
