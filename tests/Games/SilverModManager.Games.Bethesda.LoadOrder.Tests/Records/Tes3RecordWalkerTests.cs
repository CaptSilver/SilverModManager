using System.Text;
using FluentAssertions;

namespace SilverModManager.Games.Bethesda.LoadOrder.Tests.Records;

// Follows esplugin's record_id.rs and record.rs (record_id_subrecord_types, the CELL, PGRD and SCPT id mappers).
public class Tes3RecordWalkerTests
{
    private static ulong[] Walk(params Tes3RecordBlock[] records)
        => PluginRecordWalker.ReadNamedIds(new MemoryStream(PluginBytes.Write(PluginFormat.Tes3, new PluginSpec { Blocks = records })), "Test.esp");

    private static byte[] Text(string value) => [.. Encoding.ASCII.GetBytes(value), 0];

    private static Tes3RecordBlock Named(string type, string name) => new(type, ("NAME", Text(name)));

    private static byte[] CellData(byte flags, int gridX, int gridY)
    {
        var data = new byte[12];
        data[0] = flags;
        BitConverter.GetBytes(gridX).CopyTo(data, 4);
        BitConverter.GetBytes(gridY).CopyTo(data, 8);
        return data;
    }

    [Fact]
    public void TheHeaderRecordHasNoId()
    {
        Walk().Should().BeEmpty();
    }

    [Theory]
    [InlineData("GMST")]
    [InlineData("GLOB")]
    [InlineData("WEAP")]
    [InlineData("NPC_")]
    [InlineData("DIAL")]
    [InlineData("LEVC")]
    public void IdentifiesMostRecordsByTheirNameSubrecord(string type)
    {
        Walk(Named(type, "sGold")).Should().Equal(PluginRecordWalker.NamedId(type, Text("sGold")));
    }

    [Fact]
    public void TheSameNameInTheSameNamespaceIsTheSameRecord()
    {
        // Most record types share one namespace, so a weapon and an NPC with the same name are the same id.
        PluginRecordWalker.NamedId("WEAP", Text("x")).Should().Be(PluginRecordWalker.NamedId("NPC_", Text("x")));
    }

    [Fact]
    public void TheSameNameInDifferentNamespacesIsADifferentRecord()
    {
        PluginRecordWalker.NamedId("RACE", Text("x")).Should().NotBe(PluginRecordWalker.NamedId("CLAS", Text("x")));
        PluginRecordWalker.NamedId("RACE", Text("x")).Should().NotBe(PluginRecordWalker.NamedId("WEAP", Text("x")));
    }

    [Theory]
    [InlineData("RACE"), InlineData("CLAS"), InlineData("BSGN"), InlineData("SCPT"), InlineData("CELL"), InlineData("FACT"), InlineData("SOUN")]
    [InlineData("GLOB"), InlineData("REGN"), InlineData("SKIL"), InlineData("MGEF"), InlineData("LAND"), InlineData("PGRD"), InlineData("DIAL")]
    public void EachNamedNamespaceIsItsOwn(string type)
    {
        PluginRecordWalker.NamedId(type, Text("x")).Should().NotBe(PluginRecordWalker.NamedId("WEAP", Text("x")));
    }

    [Theory]
    [InlineData("SKIL")]
    [InlineData("MGEF")]
    public void IdentifiesSkillsAndMagicEffectsByTheirIndex(string type)
    {
        var index = BitConverter.GetBytes(5);

        Walk(new Tes3RecordBlock(type, ("INDX", index), ("DESC", Text("ignored")))).Should().Equal(PluginRecordWalker.NamedId(type, index));
    }

    [Fact]
    public void IdentifiesInfoByItsInamAndLandByItsIntv()
    {
        Walk(new Tes3RecordBlock("INFO", ("INAM", Text("1234")))).Should().Equal(PluginRecordWalker.NamedId("INFO", Text("1234")));
        Walk(new Tes3RecordBlock("LAND", ("INTV", new byte[8] { 1, 0, 0, 0, 2, 0, 0, 0 }))).Should().Equal(PluginRecordWalker.NamedId("LAND", new byte[] { 1, 0, 0, 0, 2, 0, 0, 0 }));
    }

    [Fact]
    public void FindsTheNameSubrecordAfterOthers()
    {
        Walk(new Tes3RecordBlock("STAT", ("FNAM", Text("a model")), ("NAME", Text("a_static")))).Should().Equal(PluginRecordWalker.NamedId("STAT", Text("a_static")));
    }

    [Fact]
    public void UsesTheFirstSubrecordOfTheTypeWhenThereAreTwo()
    {
        Walk(new Tes3RecordBlock("STAT", ("NAME", Text("first")), ("NAME", Text("second")))).Should().Equal(PluginRecordWalker.NamedId("STAT", Text("first")));
    }

    [Fact]
    public void IdentifiesAScriptByTheFirst32BytesOfItsHeader()
    {
        var header = new byte[52];
        Encoding.ASCII.GetBytes("MyScript").CopyTo(header, 0);
        header[40] = 9; // After the name, so it is not part of the id.

        Walk(new Tes3RecordBlock("SCPT", ("SCHD", header))).Should().Equal(PluginRecordWalker.NamedId("SCPT", header.AsSpan(0, 32)));
    }

    [Fact]
    public void ARecordWithNoIdSubrecordHasNoId()
    {
        Walk(new Tes3RecordBlock("STAT", ("FNAM", Text("model")))).Should().BeEmpty();
    }

    [Fact]
    public void AScriptHeaderShorterThan32BytesHasNoId()
    {
        Walk(new Tes3RecordBlock("SCPT", ("SCHD", new byte[10]))).Should().BeEmpty();
    }

    [Fact]
    public void ARecordTypeTheGameDoesNotNameHasNoId()
    {
        Walk(new Tes3RecordBlock("ZZZZ", ("NAME", Text("x")))).Should().BeEmpty();
    }

    [Fact]
    public void AnExteriorCellIsIdentifiedByItsGridAndAnInteriorByItsName()
    {
        var exterior = CellData(flags: 0, gridX: 3, gridY: -4);
        var interior = CellData(flags: 1, gridX: 3, gridY: -4);

        Walk(new Tes3RecordBlock("CELL", ("NAME", Text("Seyda Neen")), ("DATA", exterior)))
            .Should().Equal(PluginRecordWalker.NamedId("CELL", exterior.AsSpan(4)));
        Walk(new Tes3RecordBlock("CELL", ("NAME", Text("Arrille's Tradehouse")), ("DATA", interior)))
            .Should().Equal(PluginRecordWalker.NamedId("CELL", Text("Arrille's Tradehouse")));
    }

    [Fact]
    public void TwoExteriorCellsAtTheSameGridAreTheSameRecordWhateverTheyAreCalled()
    {
        var a = Walk(new Tes3RecordBlock("CELL", ("NAME", Text("")), ("DATA", CellData(0, 1, 2))));
        var b = Walk(new Tes3RecordBlock("CELL", ("NAME", Text("Named later")), ("DATA", CellData(0, 1, 2))));

        a.Should().Equal(b);
    }

    [Fact]
    public void AnInteriorCellWithNoNameHasNoId()
    {
        Walk(new Tes3RecordBlock("CELL", ("NAME", new byte[] { 0 }), ("DATA", CellData(1, 0, 0)))).Should().BeEmpty();
    }

    [Fact]
    public void ACellWhoseSubrecordsAreNotNameThenDataHasNoId()
    {
        Walk(new Tes3RecordBlock("CELL", ("DATA", CellData(0, 1, 2)), ("NAME", Text("x")))).Should().BeEmpty();
        Walk(new Tes3RecordBlock("CELL", ("NAME", Text("x")))).Should().BeEmpty();
    }

    [Fact]
    public void ACellWhoseDataIsNotTwelveBytesIsIdentifiedByItsName()
    {
        Walk(new Tes3RecordBlock("CELL", ("NAME", Text("Odd")), ("DATA", new byte[8]))).Should().Equal(PluginRecordWalker.NamedId("CELL", Text("Odd")));
    }

    [Fact]
    public void APathGridIsIdentifiedByItsGridUnlessThatIsEmptyThenByItsName()
    {
        var grid = new byte[] { 1, 0, 0, 0, 2, 0, 0, 0, 7, 0 };

        Walk(new Tes3RecordBlock("PGRD", ("DATA", grid), ("NAME", Text("Somewhere")))).Should().Equal(PluginRecordWalker.NamedId("PGRD", grid.AsSpan(0, 8)));
        Walk(new Tes3RecordBlock("PGRD", ("DATA", new byte[10]), ("NAME", Text("Somewhere")))).Should().Equal(PluginRecordWalker.NamedId("PGRD", Text("Somewhere")));
        Walk(new Tes3RecordBlock("PGRD", ("DATA", new byte[4]), ("NAME", Text("Short")))).Should().Equal(PluginRecordWalker.NamedId("PGRD", Text("Short")));
    }

    [Fact]
    public void ReturnsTheIdsSortedSoTwoPluginsCanBeCompared()
    {
        var ids = Walk(Named("GMST", "z"), Named("GMST", "a"), Named("GMST", "m"), Named("GMST", "b"));

        ids.Should().BeInAscendingOrder().And.HaveCount(4);
    }

    [Fact]
    public void KeepsARecordThatAppearsTwice()
    {
        Walk(Named("GMST", "a"), Named("GMST", "a")).Should().HaveCount(2);
    }

    [Fact]
    public void ReadsTheSameIdsFromAStreamThatCannotSeek()
    {
        var bytes = PluginBytes.Write(PluginFormat.Tes3, new PluginSpec { Blocks = [Named("GMST", "a"), new Tes3RecordBlock("STAT", ("FNAM", new byte[9000])), Named("GMST", "b")] });

        PluginRecordWalker.ReadNamedIds(new NonSeekableStream(bytes), "Test.esp")
            .Should().BeEquivalentTo([PluginRecordWalker.NamedId("GMST", Text("a")), PluginRecordWalker.NamedId("GMST", Text("b"))]);
    }

    [Fact]
    public void RejectsAPluginThatEndsInsideARecordBody()
    {
        var bytes = PluginBytes.Write(PluginFormat.Tes3, new PluginSpec { Blocks = [Named("GMST", "sGold")] });

        var act = () => PluginRecordWalker.ReadNamedIds(new MemoryStream(bytes[..^3]), "Short.esp");

        act.Should().Throw<InvalidPluginException>().WithMessage("Short.esp*");
    }

    [Fact]
    public void RejectsASubrecordThatRunsPastItsRecord()
    {
        var bytes = PluginBytes.Write(PluginFormat.Tes3, new PluginSpec { Blocks = [Named("GMST", "sGold")] });
        // The NAME subrecord's length sits 4 bytes after its type, at the very end of the record's header + 8.
        var nameLength = bytes.Length - Text("sGold").Length - 4;
        BitConverter.GetBytes(500u).CopyTo(bytes, nameLength);

        var act = () => PluginRecordWalker.ReadNamedIds(new MemoryStream(bytes), "Bad.esp");

        act.Should().Throw<InvalidPluginException>().WithMessage("Bad.esp*");
    }

    [Fact]
    public void RejectsARecordSizeLargerThanTheFile()
    {
        var bytes = PluginBytes.Write(PluginFormat.Tes3, new PluginSpec { Blocks = [Named("GMST", "sGold")] });
        var recordStart = bytes.Length - (16 + 8 + Text("sGold").Length);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, recordStart + 4);

        var act = () => PluginRecordWalker.ReadNamedIds(new MemoryStream(bytes), "Huge.esp");

        act.Should().Throw<InvalidPluginException>();
    }

    [Fact]
    public void RefusesAFormatWithFormIds()
    {
        var act = () => PluginRecordWalker.ReadNamedIds(new MemoryStream(PluginBytes.Write(PluginFormat.Tes4, new PluginSpec())), "Skyrim.esm");

        act.Should().Throw<InvalidPluginException>("the file does not start with a TES3 record");
    }
}
