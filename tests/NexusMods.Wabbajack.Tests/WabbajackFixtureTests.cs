using System.Text;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Tests.Fixtures;

namespace NexusMods.Wabbajack.Tests;

public class WabbajackFixtureTests(TemporaryFileManager temporaryFileManager)
{
    [Fact]
    public async Task AWrittenTwoDirectiveListReadsBackWithItsBlobs()
    {
        var hash = Hash.From(0x1122334455667788UL);
        var list = WabbajackFixture.EmptyList(
            "Round trip",
            new InlineFile(RelativePath.FromUnsanitizedInput("mods/A/a.txt"), hash, 3, "blob-a"),
            new FromArchive(RelativePath.FromUnsanitizedInput("mods/A/b.esp"), hash, 10, new ArchiveHashPath(hash, [RelativePath.FromUnsanitizedInput("Data/b.esp")])));
        var blobs = new Dictionary<string, byte[]> { ["blob-a"] = Encoding.UTF8.GetBytes("abc") };

        await using var folder = temporaryFileManager.CreateFolder();
        var path = WabbajackFixture.Write(folder.Path, list, blobs);

        using var zip = System.IO.Compression.ZipFile.OpenRead(path.ToString());
        zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo("modlist", "blob-a");
        using var modlist = zip.GetEntry("modlist")!.Open();
        var read = ModListReader.Read(modlist);

        read.Name.Should().Be("Round trip");
        read.Directives.Should().HaveCount(2);
        ((InlineFile)read.Directives[0]).SourceDataId.Should().Be("blob-a");
        read.Directives[0].To.Should().Be(RelativePath.FromUnsanitizedInput("mods/A/a.txt"));
        ((FromArchive)read.Directives[1]).Source.Parts.Should().Equal(RelativePath.FromUnsanitizedInput("Data/b.esp"));
        read.Directives.Select(d => d.XxHash64).Should().OnlyContain(h => h == hash);
    }
}
