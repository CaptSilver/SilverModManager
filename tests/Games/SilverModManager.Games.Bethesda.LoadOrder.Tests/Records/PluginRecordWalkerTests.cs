using FluentAssertions;

namespace SilverModManager.Games.Bethesda.LoadOrder.Tests.Records;

public class PluginRecordWalkerTests
{
    private static uint[] Walk(PluginFormat format, params PluginBlock[] blocks)
        => PluginRecordWalker.ReadFormIds(new MemoryStream(PluginBytes.Write(format, new PluginSpec { Blocks = blocks })), format, "Test.esp");

    [Theory]
    [InlineData(PluginFormat.Tes4Oblivion)]
    [InlineData(PluginFormat.Tes4)]
    public void ReturnsTheFormIdOfEveryRecordInFileOrder(PluginFormat format)
    {
        var ids = Walk(format,
            new GroupBlock("WEAP", new RecordBlock("WEAP", 0x0000_0800), new RecordBlock("WEAP", 0x0100_0801, DataSize: 37)),
            new GroupBlock("ARMO", new RecordBlock("ARMO", 0x0000_0802, DataSize: 5)));

        ids.Should().Equal(0x0000_0800u, 0x0100_0801u, 0x0000_0802u);
    }

    [Fact]
    public void ReadsTheRecordsOfNestedGroups()
    {
        var ids = Walk(PluginFormat.Tes4,
            new GroupBlock("CELL",
                new GroupBlock("0001", new GroupBlock("0002", new RecordBlock("CELL", 0x0000_0810), new RecordBlock("REFR", 0x0000_0811))),
                new RecordBlock("CELL", 0x0000_0812)),
            new GroupBlock("STAT", new RecordBlock("STAT", 0x0000_0813)));

        ids.Should().Equal(0x810u, 0x811u, 0x812u, 0x813u);
    }

    [Fact]
    public void SkipsTheBodyOfACompressedRecordByItsStoredSize()
    {
        // The header is never compressed, and its size is the stored one, so a compressed body is skipped like any other.
        const uint compressed = 0x0004_0000;
        var ids = Walk(PluginFormat.Tes4,
            new GroupBlock("NPC_", new RecordBlock("NPC_", 0x0000_0820, Flags: compressed, DataSize: 100), new RecordBlock("NPC_", 0x0000_0821)));

        ids.Should().Equal(0x820u, 0x821u);
    }

    [Fact]
    public void LeavesOutARecordWithFormIdZero()
    {
        var ids = Walk(PluginFormat.Tes4, new GroupBlock("GMST", new RecordBlock("GMST", 0), new RecordBlock("GMST", 0x0000_0830)));

        ids.Should().Equal(0x830u);
    }

    [Fact]
    public void ReturnsNothingForAPluginWithNoGroups()
    {
        Walk(PluginFormat.Tes4).Should().BeEmpty();
    }

    [Fact]
    public void ReadsTheSameRecordsFromAStreamThatCannotSeek()
    {
        var bytes = PluginBytes.Write(PluginFormat.Tes4, new PluginSpec
        {
            Blocks = [new GroupBlock("WEAP", new RecordBlock("WEAP", 0x0000_0840, DataSize: 9000), new RecordBlock("WEAP", 0x0000_0841))],
        });

        var ids = PluginRecordWalker.ReadFormIds(new NonSeekableStream(bytes), PluginFormat.Tes4, "Test.esp");

        ids.Should().Equal(0x840u, 0x841u);
    }

    [Fact]
    public void RejectsAGroupSizeSmallerThanItsOwnHeader()
    {
        var bytes = PluginBytes.Write(PluginFormat.Tes4, new PluginSpec { Blocks = [new GroupBlock("WEAP", new RecordBlock("WEAP", 0x800))] });
        var groupSize = PluginBytes.Write(PluginFormat.Tes4, new PluginSpec()).Length + 4;
        BitConverter.GetBytes(10u).CopyTo(bytes, groupSize);

        var act = () => PluginRecordWalker.ReadFormIds(new MemoryStream(bytes), PluginFormat.Tes4, "Bad.esp");

        act.Should().Throw<InvalidPluginException>().WithMessage("Bad.esp*");
    }

    [Fact]
    public void RejectsAPluginThatEndsInsideARecordBody()
    {
        var bytes = PluginBytes.Write(PluginFormat.Tes4, new PluginSpec { Blocks = [new GroupBlock("WEAP", new RecordBlock("WEAP", 0x800, DataSize: 50))] });

        var act = () => PluginRecordWalker.ReadFormIds(new MemoryStream(bytes[..^10]), PluginFormat.Tes4, "Short.esp");

        act.Should().Throw<InvalidPluginException>().WithMessage("Short.esp*");
    }

    [Fact]
    public void RejectsARecordWhereATopLevelGroupBelongs()
    {
        var act = () => Walk(PluginFormat.Tes4, new RecordBlock("WEAP", 0x800));

        act.Should().Throw<InvalidPluginException>();
    }

    [Fact]
    public void ReadsGroupsNestedFarDeeperThanAnyGameWritesWithoutRunningOutOfStack()
    {
        // A stack overflow cannot be caught and would take the app down with it, so a hostile plugin nesting tens of
        // thousands of groups must still be walked. The walk runs on a small stack to make any recursion overflow.
        const int depth = 50_000;
        const int headerLength = 24;
        var bytes = new List<byte>(PluginBytes.Write(PluginFormat.Tes4, new PluginSpec()));
        for (var level = 0; level < depth; level++)
        {
            // Each group holds the groups below it and the one record at the bottom.
            var size = (uint)((depth - level) * headerLength + headerLength);
            bytes.AddRange("GRUP"u8.ToArray());
            bytes.AddRange(BitConverter.GetBytes(size));
            bytes.AddRange(new byte[headerLength - 8]);
        }

        bytes.AddRange("STAT"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes(0u));
        bytes.AddRange(BitConverter.GetBytes(0u));
        bytes.AddRange(BitConverter.GetBytes(0x0000_0850u));
        bytes.AddRange(new byte[headerLength - 16]);

        uint[]? ids = null;
        var walk = new Thread(() => ids = PluginRecordWalker.ReadFormIds(new MemoryStream([.. bytes]), PluginFormat.Tes4, "Deep.esp"), maxStackSize: 256 * 1024);
        walk.Start();
        walk.Join();

        ids.Should().Equal(0x850u);
    }

    [Fact]
    public void RefusesMorrowindPlugins()
    {
        var act = () => PluginRecordWalker.ReadFormIds(new MemoryStream(PluginBytes.Write(PluginFormat.Tes3, new PluginSpec())), PluginFormat.Tes3, "Morrowind.esm");

        act.Should().Throw<ArgumentException>();
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
