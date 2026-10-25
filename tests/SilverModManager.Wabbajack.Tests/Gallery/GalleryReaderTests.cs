using FluentAssertions;
using SilverModManager.Sdk.Games;
using SilverModManager.Wabbajack.Gallery;

namespace SilverModManager.Wabbajack.Tests.Gallery;

public class GalleryReaderTests
{
    private static IReadOnlyList<GalleryEntry> Official() => GalleryReader.ReadModlists(GalleryFixtureFiles.Read("modlists-official.json"), "wabbajack-tools/mod-lists");

    [Fact]
    public void ReadsEverythingACardNeeds()
    {
        var entry = Official().Single(e => e.MachineUrl == "example-skyrim");

        entry.Title.Should().Be("Example Skyrim List");
        entry.Description.Should().Be("A small fixture list.");
        entry.Author.Should().Be("Some Author");
        entry.Maintainers.Should().Equal("Some Author", "A Helper");
        entry.GalleryGame.Should().Be("skyrimspecialedition");
        entry.Game.Should().Be(GameId.From("CreationEngine.SkyrimSE"));
        entry.Version.Should().Be("1.2.3");
        entry.Tags.Should().Equal("Gameplay", "Quests");
        entry.IsOfficial.Should().BeTrue();
        entry.IsNsfw.Should().BeFalse();
        entry.IsUtility.Should().BeFalse();
        entry.IsForcedDown.Should().BeFalse();
        entry.Repository.Should().Be("wabbajack-tools/mod-lists");
        entry.Image.Should().Be(new Uri("https://raw.githubusercontent.com/wabbajack-tools/mod-lists/master/images/example.webp"));
        entry.Readme.Should().Be(new Uri("https://example.invalid/readme"));
        entry.Download.Should().Be(new Uri("https://authored-files.wabbajack.org/Example_Skyrim_List.wabbajack_0123456789abcdef"));
    }

    [Fact]
    public void SizesAreNumbers()
    {
        var entry = Official().Single(e => e.MachineUrl == "example-skyrim");

        entry.DownloadSize.Should().Be(4096UL);
        entry.ArchiveCount.Should().Be(120);
        entry.ArchivesSize.Should().Be(52428800UL);
        entry.InstalledFileCount.Should().Be(900);
        entry.InstalledSize.Should().Be(104857600UL);
        entry.DownloadHash!.Value.Value.Should().Be(0x0807060504030201UL);
        entry.IsDownloadable.Should().BeTrue();
    }

    [Fact]
    public void FlagsComeFromTheirFields()
    {
        var entries = Official();

        entries.Single(e => e.MachineUrl == "spicy-fallout").IsNsfw.Should().BeTrue();
        entries.Single(e => e.MachineUrl == "taken-down").IsForcedDown.Should().BeTrue();
        entries.Single(e => e.MachineUrl == "tool").IsUtility.Should().BeTrue();
    }

    [Fact]
    public void AnEntryThatIsNotAnObjectOrHasNoTitleIsSkipped()
    {
        var entries = Official();

        entries.Should().HaveCount(5);
        entries.Select(e => e.Author).Should().NotContain("No title at all");
    }

    [Fact]
    public void AGameTheAppHasNoTitleForIsKeptWithoutAGame()
    {
        Official().Single(e => e.MachineUrl == "sdv").Game.Should().BeNull();
    }

    [Fact]
    public void BadFieldsAreDroppedNotFatal()
    {
        var entry = GalleryReader.ReadModlists(GalleryFixtureFiles.Read("modlists-community.json"), "someone/community-lists").Single();

        entry.Title.Should().Be("Community Oblivion List");
        entry.Download.Should().BeNull("only https links are followed");
        entry.Readme.Should().BeNull();
        entry.Image.Should().Be(new Uri("https://example.invalid/i.png"));
        entry.DownloadHash.Should().BeNull();
        entry.DownloadSize.Should().BeNull();
        entry.ArchiveCount.Should().BeNull();
        entry.Tags.Should().Equal("Vanilla+");
        entry.IsDownloadable.Should().BeFalse();
    }

    [Theory]
    [InlineData("{ \"this\": \"is an object\" }")]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("null")]
    public void JunkThatIsNotAListThrows(string json)
    {
        var act = () => GalleryReader.ReadModlists(json, "x/y");

        act.Should().Throw<GalleryFormatException>();
    }

    [Fact]
    public void RepositoriesAreReadAsNameToAddress()
    {
        var repositories = GalleryReader.ReadRepositories(GalleryFixtureFiles.Read("repositories.json"));

        repositories.Select(r => r.Name).Should().Equal("wabbajack-tools/mod-lists", "someone/community-lists", "broken/server-error", "broken/junk");
        repositories[0].Url.Should().Be(GalleryFixtureFiles.Official);
    }

    [Fact]
    public void RepositoriesThatAreNotAnObjectThrow()
    {
        var act = () => GalleryReader.ReadRepositories("[1, 2]");

        act.Should().Throw<GalleryFormatException>();
    }
}
