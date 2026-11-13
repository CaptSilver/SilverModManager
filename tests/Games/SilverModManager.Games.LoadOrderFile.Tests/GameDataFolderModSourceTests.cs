using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Games.LoadOrderFile.Manifests;

namespace SilverModManager.Games.LoadOrderFile.Tests;

public class GameDataFolderModSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "game-data-folder-source-" + Guid.NewGuid().ToString("N"));

    private AbsolutePath Game => FileSystem.Shared.FromUnsanitizedFullPath(_root);

    private static GameDataFolderModSource Core() => new("Data", new AboutXmlManifestReader(), id => id == "ludeon.rimworld", ExternalListing.Always);

    private static GameDataFolderModSource Expansions() => new("Data", new AboutXmlManifestReader(), id => id.StartsWith("ludeon.rimworld."), ExternalListing.WhenListed);

    private void About(string folder, string packageId)
    {
        var file = Path.Combine(_root, "Data", folder, "About/About.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, $"<ModMetaData><packageId>{packageId}</packageId></ModMetaData>");
    }

    [Fact]
    public void CorePassesTheCoreFilter()
    {
        About("Core", "Ludeon.RimWorld");

        Core().Find(Game).Should().Equal("ludeon.rimworld");
    }

    [Fact]
    public void RoyaltyPassesTheExpansionFilterAndCoreDoesNot()
    {
        About("Core", "Ludeon.RimWorld");
        About("Royalty", "Ludeon.RimWorld.Royalty");

        Expansions().Find(Game).Should().Equal("ludeon.rimworld.royalty");
        Core().Find(Game).Should().Equal("ludeon.rimworld");
    }

    [Fact]
    public void AFolderWithoutAManifestIsSkipped()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Data", "Half"));
        About("Core", "Ludeon.RimWorld");

        Core().Find(Game).Should().Equal("ludeon.rimworld");
    }

    [Fact]
    public void NoDataFolderYieldsNothing()
    {
        Core().Find(Game).Should().BeEmpty();
    }

    [Fact]
    public void FoldersComeInOrdinalOrder()
    {
        About("Royalty", "Ludeon.RimWorld.Royalty");
        About("Anomaly", "Ludeon.RimWorld.Anomaly");

        Expansions().Find(Game).Should().Equal("ludeon.rimworld.anomaly", "ludeon.rimworld.royalty");
    }

    [Fact]
    public void TheListingIsTheOneTheSourceWasGiven()
    {
        Core().Listing.Should().Be(ExternalListing.Always);
        Expansions().Listing.Should().Be(ExternalListing.WhenListed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
