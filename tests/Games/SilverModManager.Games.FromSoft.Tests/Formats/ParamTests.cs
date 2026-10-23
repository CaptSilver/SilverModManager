using System.Buffers.Binary;
using System.Collections.Immutable;
using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests.Formats;

public class ParamTests
{
    private static readonly FromSoftFormatTraits Traits = EldenRingGame.Definition.Formats;

    private static readonly byte[] GoldenSynthetic = GoldenFilesTests.Read("param_synthetic.param");
    private static readonly byte[] GoldenRewritten = GoldenFilesTests.Read("param_synthetic_rewritten.param");
    private static readonly byte[] GoldenPatched = GoldenFilesTests.Read("param_synthetic_patched.param");

    private static byte[] Rows8(byte b) => Enumerable.Repeat(b, 8).ToArray();

    private static Dictionary<int, byte[]> Rows(params (int Id, byte Fill)[] rows) => rows.ToDictionary(r => r.Id, r => Rows8(r.Fill));

    private static byte[] Make(params int[] ids) => ParamFixture.Make(ids);

    // ---- goldens

    [Fact]
    public void TheFixtureReproducesTheBytesErmBuiltForTheSameArguments()
        => ParamFixture.Make([1, 2, 5], stride: 8, tail: "SP_"u8.ToArray()).Should().Equal(GoldenSynthetic);

    [Fact]
    public void WritingWhatWasReadMatchesWhatErmWrote()
        => Param.Write(Param.Read(GoldenSynthetic)).Should().Equal(GoldenRewritten);

    [Fact]
    public void PatchingTheGoldenMatchesWhatErmWrote()
        => Param.Write(Param.PatchRows(Param.Read(GoldenSynthetic), insert: Rows((7, 7)))).Should().Equal(GoldenPatched);

    [Fact]
    public void TheRegulationFixtureReproducesTheBytesErmPackedForTheSameParams()
    {
        var tables = new Dictionary<int, (string, IReadOnlyDictionary<int, byte[]>, int)>
        {
            [1] = ("SP_EFFECT_PARAM_ST", new Dictionary<int, byte[]> { [1] = Rows8(1), [5] = Rows8(5) }, 8),
            [2] = ("OTHER_ST", new Dictionary<int, byte[]> { [9] = [9, 9, 9, 9] }, 4),
        };
        RegulationFixture.Build(tables, "11701000", Traits).Should().Equal(GoldenFilesTests.Read("regulation_params.bin"));
    }

    // ---- read

    [Fact]
    public void ReadExposesIdsStrideAndRowData()
    {
        var table = Param.Read(Make(10, 20, 30));
        table.Rows.Select(r => r.Id).Should().Equal(10, 20, 30);
        table.Stride.Should().Be(8);
        table.Rows[1].Data.Should().Equal(Rows8(20));
        table.ParamType.Should().Be("TEST_PARAM_ST");
    }

    [Fact]
    public void ReadExposesTheParamdefVersionAndFormatFlags()
    {
        var table = Param.Read(Make(1));
        table.ParamdefDataVersion.Should().Be(4);
        table.FormatFlags.ToArray().Should().Equal(ParamFixture.DefaultFormatFlags);
    }

    [Fact]
    public void ARoundTripIsByteIdentical()
    {
        var blob = Make(1, 2, 3, 900);
        Param.Write(Param.Read(blob)).Should().Equal(blob);
    }

    [Fact]
    public void ARoundTripOfASingleRowParamIsByteIdentical()
    {
        // One row has no offset gap to derive a stride from, so this takes the distance-to-strings fallback.
        var blob = ParamFixture.Make([7], stride: 64);
        var table = Param.Read(blob);
        table.Stride.Should().Be(64);
        Param.Write(table).Should().Equal(blob);
    }

    [Fact]
    public void ARoundTripPreservesTheBytesBetweenTheLastRowAndTheStrings()
    {
        var blob = ParamFixture.Make([1, 2], tail: "ASSET_MA"u8.ToArray());
        var table = Param.Read(blob);
        table.Tail.Should().Equal("ASSET_MA"u8.ToArray());
        Param.Write(table).Should().Equal(blob);
    }

    [Fact]
    public void ARoundTripPreservesTheOrderOfAParamThatShipsOutOfOrder()
    {
        var blob = Make(910, 800);
        Param.Write(Param.Read(blob)).Should().Equal(blob);
    }

    [Fact]
    public void ReadOfAZeroRowParamRoundTrips()
    {
        var blob = Make();
        var table = Param.Read(blob);
        table.Rows.Should().BeEmpty();
        Param.Write(table).Should().Equal(blob);
    }

    [Fact]
    public void ParamTypeIsEmptyWhenTheNameSitsOutsideTheStringsBlock()
    {
        var blob = ParamFixture.Make([1, 2], tail: "TYPE"u8.ToArray());
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(0x10), BinaryPrimitives.ReadUInt32LittleEndian(blob) - 4);
        Param.Read(blob).ParamType.Should().BeEmpty();
    }

    // ---- replace and patch

    [Fact]
    public void InsertingARowGrowsTheFileByStrideAndATableEntry()
    {
        var blob = Make(10, 30);
        var table = Param.Read(blob);
        var grown = Param.ReplaceRows(table, [..table.Rows, new ParamRow(20, Rows8(20), table.Rows[0].NameOffset)]);
        Param.Write(grown).Length.Should().Be(blob.Length + table.Stride + 24);
    }

    [Fact]
    public void ReplaceRowsKeepsIdsAscending()
    {
        var table = Param.Read(Make(10, 30));
        var grown = Param.ReplaceRows(table, [..table.Rows, new ParamRow(20, Rows8(0x20), table.Rows[0].NameOffset)]);
        Param.Read(Param.Write(grown)).Rows.Select(r => r.Id).Should().Equal(10, 20, 30);
    }

    [Fact]
    public void ReplaceRowsRewritesOffsetsSoTheResultReparses()
    {
        var table = Param.Read(Make(1, 2));
        var grown = Param.ReplaceRows(table, [..table.Rows, new ParamRow(3, Rows8(0xAA), table.Rows[0].NameOffset)]);
        var reparsed = Param.Read(Param.Write(grown));
        reparsed.Rows.Select(r => r.Id).Should().Equal(1, 2, 3);
        reparsed.Rows[2].Data.Should().Equal(Rows8(0xAA));
        reparsed.ParamType.Should().Be("TEST_PARAM_ST");
    }

    [Fact]
    public void TheHeaderCountAndOffsetsStayConsistentAfterAWrite()
    {
        var table = Param.Read(Make(5, 6));
        var written = Param.Write(Param.ReplaceRows(table, [..table.Rows, new ParamRow(7, Rows8(7), table.Rows[0].NameOffset)]));
        var stringsOffset = BinaryPrimitives.ReadUInt32LittleEndian(written);
        var dataOffset = BinaryPrimitives.ReadInt64LittleEndian(written.AsSpan(0x30));
        BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(0x0A)).Should().Be(3);
        dataOffset.Should().Be(0x40 + 3 * 24);
        stringsOffset.Should().Be((uint)(dataOffset + 3 * 8));
    }

    [Fact]
    public void WriteRefusesRowsThatAreNotTheStride()
    {
        var table = Param.Read(Make(1, 2));
        FluentActions.Invoking(() => Param.Write(Param.ReplaceRows(table, [new ParamRow(1, [1, 2, 3, 4, 5], table.Rows[0].NameOffset)])))
            .Should().Throw<ParamException>().WithMessage("*stride*");
    }

    [Fact]
    public void WriteToleratesTheDuplicateIdsShippedParamsCarry()
    {
        var blob = Make(1, 2, 2, 3);
        Param.Write(Param.Read(blob)).Should().Equal(blob);
    }

    [Fact]
    public void PatchRefusesToInsertAnIdThatAlreadyExists()
        => FluentActions.Invoking(() => Param.PatchRows(Param.Read(Make(1, 2)), insert: Rows((2, 2))))
            .Should().Throw<ParamException>().WithMessage("*already exist*");

    [Fact]
    public void PatchRefusesToOverwriteAnIdThatIsAbsent()
        => FluentActions.Invoking(() => Param.PatchRows(Param.Read(Make(1, 2)), overwrite: Rows((99, 0x99))))
            .Should().Throw<ParamException>().WithMessage("*not in this param*");

    [Fact]
    public void PatchRefusesAParamWithAmbiguousDuplicateIds()
        => FluentActions.Invoking(() => Param.PatchRows(Param.Read(Make(1, 2, 2)), insert: Rows((5, 5))))
            .Should().Throw<ParamException>().WithMessage("*duplicate*");

    [Fact]
    public void PatchRefusesAParamWhoseIdsAreNotAscending()
        => FluentActions.Invoking(() => Param.PatchRows(Param.Read(Make(910, 800)), insert: Rows((5, 5))))
            .Should().Throw<ParamException>().WithMessage("*not ascending*");

    [Fact]
    public void PatchOverwritesAndInsertsInOnePass()
    {
        var table = Param.Read(Make(10, 30));
        var patched = Param.Read(Param.Write(Param.PatchRows(table, overwrite: Rows((10, 0xEE)), insert: Rows((20, 0xDD)))));
        patched.Rows.Select(r => r.Id).Should().Equal(10, 20, 30);
        patched.Rows[0].Data.Should().Equal(Rows8(0xEE));
        patched.Rows[1].Data.Should().Equal(Rows8(0xDD));
        patched.Rows[2].Data.Should().Equal(Rows8(30));
    }

    [Fact]
    public void PatchLeavesEveryOtherRowByteIdentical()
    {
        var table = Param.Read(Make(1, 2, 3, 4, 5));
        var patched = Param.Read(Param.Write(Param.PatchRows(table, insert: Rows((6, 6)))));
        foreach (var row in table.Rows)
            patched.Rows.Single(r => r.Id == row.Id).Data.Should().Equal(row.Data);
    }

    [Fact]
    public void AZeroNameOffsetIsPreservedNotRelocated()
    {
        var blob = Make(1, 2);
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(0x40 + 16), 0);
        var table = Param.Read(blob);
        table.Rows[0].NameOffset.Should().Be(0);
        Param.Read(Param.Write(Param.PatchRows(table, insert: Rows((3, 3))))).Rows[0].NameOffset.Should().Be(0);
    }

    [Fact]
    public void WriteRefusesToMoveANameOffsetThatSitsBeforeTheStringsBlock()
    {
        var blob = Make(1, 2);
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(0x40 + 16), 8);
        var table = Param.Read(blob);
        Param.Write(table).Should().Equal(blob); // an unchanged row count moves nothing
        FluentActions.Invoking(() => Param.Write(Param.PatchRows(table, insert: Rows((3, 3)))))
            .Should().Throw<ParamException>().WithMessage("*before the strings block*");
    }

    [Fact]
    public void WriteRefusesToMoveAParamTypeThatStartsBeforeTheStringsBlock()
    {
        var blob = ParamFixture.Make([1, 2], tail: "TYPE"u8.ToArray());
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(0x10), BinaryPrimitives.ReadUInt32LittleEndian(blob) - 4);
        var table = Param.Read(blob);
        Param.Write(table).Should().Equal(blob);
        FluentActions.Invoking(() => Param.Write(Param.PatchRows(table, insert: Rows((3, 3)))))
            .Should().Throw<ParamException>().WithMessage("*param type string sits before*");
    }

    // ---- read guards

    [Fact]
    public void ReadRefusesARaggedStride()
    {
        var blob = Make(1, 2, 3);
        var position = 0x40 + 24 + 8;
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(position), BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(position)) + 4);
        FluentActions.Invoking(() => Param.Read(blob)).Should().Throw<ParamException>().WithMessage("*stride*");
    }

    [Fact]
    public void ReadRefusesATruncatedParam()
        => FluentActions.Invoking(() => Param.Read(Make(1, 2, 3)[..0x30])).Should().Throw<ParamException>().WithMessage("*truncated*");

    [Fact]
    public void ReadRefusesRowDataThatDoesNotStartWhereTheRowTableEnds()
    {
        var blob = Make(1, 2);
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(0x30), 0x40 + 2 * 24 + 8);
        FluentActions.Invoking(() => Param.Read(blob)).Should().Throw<ParamException>().WithMessage("*unexpected gap*");
    }

    [Fact]
    public void ReadRefusesAStringsOffsetPastTheEndOfTheFile()
    {
        var blob = Make(1, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(blob, (uint)blob.Length + 1);
        FluentActions.Invoking(() => Param.Read(blob)).Should().Throw<ParamException>().WithMessage("*outside the file*");
    }

    [Fact]
    public void ReadRefusesRowOffsetsThatDescend()
    {
        var blob = Make(1, 2);
        var first = BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(0x40 + 8));
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(0x40 + 8), BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(0x40 + 24 + 8)));
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(0x40 + 24 + 8), first);
        FluentActions.Invoking(() => Param.Read(blob)).Should().Throw<ParamException>().WithMessage("*not ascending*");
    }

    [Fact]
    public void ReadRefusesANameOffsetOutsideTheFile()
    {
        var blob = Make(1, 2);
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(0x40 + 16), blob.Length + 1);
        FluentActions.Invoking(() => Param.Read(blob)).Should().Throw<ParamException>().WithMessage("*name offset*");
    }

    [Fact]
    public void ReadRefusesAParamTypeOffsetOutsideTheFile()
    {
        var blob = Make(1, 2);
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(0x10), blob.Length + 1);
        FluentActions.Invoking(() => Param.Read(blob)).Should().Throw<ParamException>().WithMessage("*param type offset*");
    }

    [Fact]
    public void ReadRefusesARowOffsetBeforeTheRowDataSection()
    {
        var blob = Make(1, 2);
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(0x40 + 8), -32);
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(0x40 + 24 + 8), -24);
        FluentActions.Invoking(() => Param.Read(blob)).Should().Throw<ParamException>().WithMessage("*precedes the row-data section*");
    }

    [Fact]
    public void ReadRefusesRowsThatOverrunTheStringsBlock()
    {
        var blob = Make(1, 2, 3);
        // The last row starts one stride late, so its data would end past the strings.
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(0x40 + 2 * 24 + 8), BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(0x40 + 2 * 24 + 8)) + 8);
        FluentActions.Invoking(() => Param.Read(blob)).Should().Throw<ParamException>();
    }

    [Fact]
    public void ReadRefusesDuplicateRowOffsetsThatDeriveAZeroStride()
    {
        var blob = Make(1, 2);
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(0x40 + 24 + 8), BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(0x40 + 8)));
        FluentActions.Invoking(() => Param.Read(blob)).Should().Throw<ParamException>().WithMessage("*stride*");
    }

    [Fact]
    public void ReadRefusesASingleRowWhoseOffsetSitsPastTheStringsBlock()
    {
        var blob = ParamFixture.Make([7], stride: 64);
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(0x40 + 8), BinaryPrimitives.ReadUInt32LittleEndian(blob) + 8);
        FluentActions.Invoking(() => Param.Read(blob)).Should().Throw<ParamException>().WithMessage("*stride*");
    }

    [Fact]
    public void ReadRefusesAStrideWiderThanTheFile()
    {
        var blob = Make(1, 2);
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(0x40 + 24 + 8), 0x40 + 2 * 24 + (1L << 40));
        FluentActions.Invoking(() => Param.Read(blob)).Should().Throw<ParamException>().WithMessage("*stride*");
    }

    [Fact]
    public void ReadRefusesRowDataThatDoesNotStartAtTheDataOffset()
    {
        // Writing would re-pack the rows at the data offset, silently moving them.
        var blob = Make(1);
        BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(0x40 + 8), 0x40 + 24 + 4);
        FluentActions.Invoking(() => Param.Read(blob)).Should().Throw<ParamException>().WithMessage("*start*");
    }

    // ---- layout gate

    [Fact]
    public void TheAcceptedFlagsRoundTripUnchanged()
    {
        var table = Param.Read(Make(1, 2));
        table.FormatFlags.ToArray().Should().Equal(0x00, 0x85, 0x07, 0x00);
        Param.Write(table).AsSpan(0x2C, 4).ToArray().Should().Equal(0x00, 0x85, 0x07, 0x00);
    }

    [Fact]
    public void TheByteAfterTheOffsetFlagsDoesNotGateTheLayout()
    {
        // 0x2E only says how row names are encoded, and names are carried as opaque offsets.
        var blob = ParamFixture.Make([1, 2], formatFlags: [0x00, 0x85, 0x06, 0x00]);
        Param.Write(Param.Read(blob)).Should().Equal(blob);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0x85, 0x07, 0x00 }, "FF850700", "big-endian")]
    [InlineData(new byte[] { 0x00, 0x81, 0x07, 0x00 }, "00810700", "16-bit data offset")]
    [InlineData(new byte[] { 0x00, 0x83, 0x07, 0x00 }, "00830700", "32-bit")]
    // Flag01 with IntDataOffset means 32-bit offsets even when the 64-bit bit is also set.
    [InlineData(new byte[] { 0x00, 0x87, 0x07, 0x00 }, "00870700", "32-bit")]
    [InlineData(new byte[] { 0x00, 0x05, 0x07, 0x00 }, "00050700", "inline")]
    public void AnyOtherLayoutIsRefusedNamingAllFourFlagBytes(byte[] flags, string hex, string described)
    {
        var blob = ParamFixture.Make([1, 2], formatFlags: flags);
        FluentActions.Invoking(() => Param.Read(blob)).Should().Throw<ParamException>().WithMessage($"*{hex}*{described}*");
    }

    [Fact]
    public void ALayoutIsRefusedBeforeAnyOfItsOffsetsAreTrusted()
    {
        // A big-endian file's offsets would read as garbage; the flag gate has to answer first.
        var blob = ParamFixture.Make([1, 2], formatFlags: [0xFF, 0x85, 0x07, 0x00]);
        BinaryPrimitives.WriteUInt32LittleEndian(blob, 0xFFFFFFFF);
        FluentActions.Invoking(() => Param.Read(blob)).Should().Throw<ParamException>().WithMessage("*FF850700*");
    }

    // ---- comparable strides

    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(1, 5, false)]
    [InlineData(5, 1, false)]
    [InlineData(0, 3, false)]
    public void StridesAreComparableOnlyWhenBothTablesHaveMoreThanOneRow(int a, int b, bool expected)
        => Param.StridesComparable(a, b).Should().Be(expected);

    // ---- inside a regulation

    [Fact]
    public void ParamsRoundTripOutOfARegulationBuiltFromThem()
    {
        var tables = new Dictionary<int, (string, IReadOnlyDictionary<int, byte[]>, int)>
        {
            [1] = ("SP_EFFECT_PARAM_ST", new Dictionary<int, byte[]> { [1] = Rows8(1), [5] = Rows8(5) }, 8),
            [2] = ("OTHER_ST", new Dictionary<int, byte[]> { [9] = [9, 9, 9, 9] }, 4),
        };
        var entries = Regulation.Entries(RegulationFixture.Build(tables, "11701000", Traits), Traits);
        foreach (var entry in entries)
            Param.Write(Param.Read(entry.Data)).Should().Equal(entry.Data);
        Param.Read(entries.Single(e => e.Id == 2).Data).Rows.Single().Data.Should().Equal(9, 9, 9, 9);
    }
}
