using System.Text;
using FluentAssertions;
using NexusMods.Games.CreationEngine.Creations;

namespace NexusMods.Games.CreationEngine.Tests.Creations;

public class CreationsManifestReaderTests
{
    /// <summary>Builds a manifest the way the game lays it out: a uint16 LE length, then the bytes, per entry.</summary>
    internal static byte[] Build(params string[] entries)
    {
        using var stream = new MemoryStream();
        foreach (var entry in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(entry);
            stream.Write(BitConverter.GetBytes((ushort)bytes.Length));
            stream.Write(bytes);
        }
        return stream.ToArray();
    }

    [Fact]
    public void EntriesRoundTripWithoutTheDataPrefix()
    {
        var files = CreationsManifestReader.Read(new MemoryStream(Build("data/inventedcreation.esm", "data/inventedcreation.bsa")));

        files.Should().Equal("inventedcreation.esm", "inventedcreation.bsa");
    }

    [Fact]
    public void ThePrefixIsMatchedWithoutRegardToCase()
    {
        CreationsManifestReader.Read(new MemoryStream(Build("Data/Mixed.esp"))).Should().Equal("Mixed.esp");
    }

    [Fact]
    public void AnEmptyFileHasNoEntries()
    {
        CreationsManifestReader.Read(new MemoryStream([])).Should().BeEmpty();
    }

    [Fact]
    public void ATruncatedEntryThrowsTheTypedException()
    {
        var bytes = Build("data/inventedcreation.esm")[..^3];

        var act = () => CreationsManifestReader.Read(new MemoryStream(bytes));

        act.Should().Throw<CreationsRecordException>();
    }

    [Fact]
    public void ALoneLengthByteThrowsTheTypedException()
    {
        var act = () => CreationsManifestReader.Read(new MemoryStream([5]));

        act.Should().Throw<CreationsRecordException>();
    }

    [Theory]
    [InlineData("inventedquest-1700000000.manifest", "inventedquest")]
    [InlineData("plain.manifest", "plain")]
    public void TheIdIsTheFileNameBeforeTheFirstDash(string fileName, string expected)
    {
        CreationsManifestReader.IdOf(fileName).Should().Be(expected);
    }
}
