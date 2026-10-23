using System.Text;
using FluentAssertions;
using NexusMods.Games.CreationEngine.Creations;

namespace NexusMods.Games.CreationEngine.Tests.Creations;

public class ContentCatalogReaderTests
{
    private const string Catalog = """
        {
          "ContentCatalog": { "Description": "header", "Version": "1.0" },
          "CSV2_1": {
            "AchievementSafe": true,
            "Files": [ "ccinvented001-garden.esm", "ccinvented001-garden.bsa" ],
            "FilesSize": 1234,
            "Timestamp": 1700000000,
            "Title": "Invented Garden",
            "Version": "1.2.3"
          },
          "TM_0a1b2c3d-0000-4000-8000-000000000001": {
            "AchievementSafe": false,
            "Files": [ "inventedquest.esm" ],
            "Title": "Invented Quest",
            "Version": "150669.== Version Number =="
          }
        }
        """;

    private static IReadOnlyList<ContentCatalogEntry> Read(string json, bool bom = false)
    {
        var bytes = new List<byte>();
        if (bom) bytes.AddRange(Encoding.UTF8.GetPreamble());
        bytes.AddRange(Encoding.UTF8.GetBytes(json));
        return ContentCatalogReader.Read(new MemoryStream(bytes.ToArray()));
    }

    [Fact]
    public void TheHeaderEntryIsSkipped()
    {
        Read(Catalog).Select(e => e.Key).Should().Equal("CSV2_1", "TM_0a1b2c3d-0000-4000-8000-000000000001");
    }

    [Fact]
    public void TitleAndBareFileNamesAreRead()
    {
        var entry = Read(Catalog).First();

        entry.Title.Should().Be("Invented Garden");
        entry.Files.Should().Equal("ccinvented001-garden.esm", "ccinvented001-garden.bsa");
    }

    [Fact]
    public void ADottedVersionIsKept()
    {
        Read(Catalog).First().Version.Should().Be("1.2.3");
    }

    [Fact]
    public void AJunkVersionGivesNone()
    {
        Read(Catalog).Last().Version.Should().BeNull();
    }

    [Fact]
    public void AMissingVersionGivesNone()
    {
        Read("""{ "CSV2_9": { "Files": ["a.esm"], "Title": "T" } }""").Single().Version.Should().BeNull();
    }

    [Fact]
    public void ABomIsAccepted()
    {
        Read(Catalog, bom: true).Should().HaveCount(2);
    }

    [Fact]
    public void AMissingFilesListGivesNoFiles()
    {
        Read("""{ "CSV2_9": { "Title": "T" } }""").Single().Files.Should().BeEmpty();
    }

    [Fact]
    public void ATruncatedFileThrowsTheTypedException()
    {
        var act = () => Read(Catalog[..80]);

        act.Should().Throw<CreationsRecordException>();
    }

    [Fact]
    public void ARootThatIsNotAnObjectThrowsTheTypedException()
    {
        var act = () => Read("[1, 2]");

        act.Should().Throw<CreationsRecordException>();
    }

    [Fact]
    public void AnEntryThatIsNotAnObjectThrowsTheTypedException()
    {
        var act = () => Read("""{ "CSV2_1": 4 }""");

        act.Should().Throw<CreationsRecordException>();
    }
}
