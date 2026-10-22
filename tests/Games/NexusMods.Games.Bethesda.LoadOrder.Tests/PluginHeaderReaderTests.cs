using FluentAssertions;

namespace NexusMods.Games.Bethesda.LoadOrder.Tests;

public class PluginHeaderReaderTests
{
    private static PluginHeader Read(byte[] bytes, PluginFormat format, string name = "Test.esp")
        => PluginHeaderReader.Read(new MemoryStream(bytes), format, name);

    [Theory]
    [InlineData(PluginFormat.Tes3)]
    [InlineData(PluginFormat.Tes4Oblivion)]
    [InlineData(PluginFormat.Tes4)]
    public void RoundTripsEveryField(PluginFormat format)
    {
        var spec = new PluginSpec
        {
            RecordFlags = 0x201,
            Masters = ["Skyrim.esm", "Update.esm"],
            HeaderVersion = 1.7f,
            RecordCount = 1234,
            Description = "A description",
            Author = "Someone",
        };

        var header = Read(PluginBytes.Write(format, spec), format, "Mod.esp");

        header.FileName.Should().Be("Mod.esp");
        header.Format.Should().Be(format);
        header.Masters.Should().Equal("Skyrim.esm", "Update.esm");
        header.RecordFlags.Should().Be(0x201);
        header.HeaderVersion.Should().Be(1.7f);
        header.RecordCount.Should().Be(1234);
        header.Description.Should().Be("A description");
        header.Author.Should().Be("Someone");
    }

    [Theory]
    [InlineData(PluginFormat.Tes3)]
    [InlineData(PluginFormat.Tes4Oblivion)]
    [InlineData(PluginFormat.Tes4)]
    public void MastersKeepFileOrder(PluginFormat format)
    {
        string[] masters = ["Zeta.esm", "Alpha.esm", "Mid.esm"];

        Read(PluginBytes.Write(format, new PluginSpec { Masters = masters }), format).Masters.Should().Equal(masters);
    }

    [Theory]
    [InlineData(PluginFormat.Tes3)]
    [InlineData(PluginFormat.Tes4Oblivion)]
    [InlineData(PluginFormat.Tes4)]
    public void MissingHedrGivesNullVersionAndCount(PluginFormat format)
    {
        var bytes = PluginBytes.Write(format, new PluginSpec { HeaderVersion = null, RecordCount = null, Masters = ["A.esm"] });

        var header = Read(bytes, format);

        header.HeaderVersion.Should().BeNull();
        header.RecordCount.Should().BeNull();
        header.Masters.Should().Equal("A.esm");
    }

    [Theory]
    [InlineData(PluginFormat.Tes4Oblivion)]
    [InlineData(PluginFormat.Tes4)]
    public void Tes4WithoutDescriptionOrAuthorGivesNulls(PluginFormat format)
    {
        var header = Read(PluginBytes.Write(format, new PluginSpec()), format);

        header.Description.Should().BeNull();
        header.Author.Should().BeNull();
    }

    [Fact]
    public void StringsAreWindows1252()
    {
        var spec = new PluginSpec { Author = "Café €", Masters = ["Frühling.esm"] };

        var header = Read(PluginBytes.Write(PluginFormat.Tes4, spec), PluginFormat.Tes4);

        header.Author.Should().Be("Café €");
        header.Masters.Should().Equal("Frühling.esm");
    }

    [Fact]
    public void Tes3StringsAreNulTruncated()
    {
        var header = Read(PluginBytes.Write(PluginFormat.Tes3, new PluginSpec { Author = "Bob", Description = "Hi" }), PluginFormat.Tes3);

        header.Author.Should().Be("Bob");
        header.Description.Should().Be("Hi");
    }

    [Fact]
    public void Tes3MasterFlagComesFromHedr()
    {
        var header = Read(PluginBytes.Write(PluginFormat.Tes3, new PluginSpec { RecordFlags = 1 }), PluginFormat.Tes3);

        header.RecordFlags.Should().Be(1);
    }

    [Theory]
    [InlineData("TES4", PluginFormat.Tes3)]
    [InlineData("TES3", PluginFormat.Tes4)]
    [InlineData("TES3", PluginFormat.Tes4Oblivion)]
    [InlineData("GRUP", PluginFormat.Tes4)]
    [InlineData("TES5", PluginFormat.Tes4)]
    public void WrongRecordTypeThrows(string type, PluginFormat format)
    {
        var act = () => Read(PluginBytes.RecordOfType(type), format);

        act.Should().Throw<InvalidPluginException>();
    }

    [Theory]
    [InlineData(PluginFormat.Tes3, 10)]
    [InlineData(PluginFormat.Tes4, 10)]
    [InlineData(PluginFormat.Tes4Oblivion, 0)]
    [InlineData(PluginFormat.Tes4, 30)]
    [InlineData(PluginFormat.Tes4, -3)]
    public void TruncatedStreamThrows(PluginFormat format, int keep)
    {
        var bytes = PluginBytes.Write(format, new PluginSpec { Masters = ["A.esm"], Author = "x" });
        var length = keep >= 0 ? keep : bytes.Length + keep;

        var act = () => Read(bytes[..length], format);

        act.Should().Throw<InvalidPluginException>();
    }

    [Fact]
    public void DataSizeLargerThanTheFileThrowsWithoutAllocatingIt()
    {
        var bytes = PluginBytes.Write(PluginFormat.Tes4, new PluginSpec());
        BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, 4);

        var act = () => Read(bytes, PluginFormat.Tes4);

        act.Should().Throw<InvalidPluginException>();
    }

    [Fact]
    public void ReadsAXxxxPresizedSubrecord()
    {
        var description = new string('d', 70_000);
        var bytes = PluginBytes.Tes4WithPresizedDescription(description, 70_001);

        var header = Read(bytes, PluginFormat.Tes4);

        header.Description.Should().Be(description);
    }

    [Fact]
    public void ReadsThreeHundredMasters()
    {
        var masters = Enumerable.Range(0, 300).Select(i => $"Master{i:D3}.esm").ToArray();

        var header = Read(PluginBytes.Write(PluginFormat.Tes4, new PluginSpec { Masters = masters }), PluginFormat.Tes4);

        header.Masters.Should().Equal(masters);
    }

    [Fact]
    public void ReadsFromAStreamThatCannotSeek()
    {
        var bytes = PluginBytes.Write(PluginFormat.Tes4, new PluginSpec { Masters = ["A.esm"] });

        var header = PluginHeaderReader.Read(new NonSeekableStream(bytes), PluginFormat.Tes4, "A.esp");

        header.Masters.Should().Equal("A.esm");
    }

    [Fact]
    public void LeavesTheStreamOpen()
    {
        var stream = new MemoryStream(PluginBytes.Write(PluginFormat.Tes4, new PluginSpec()));

        PluginHeaderReader.Read(stream, PluginFormat.Tes4, "A.esp");

        stream.CanRead.Should().BeTrue();
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
