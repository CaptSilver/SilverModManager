using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Games.LoadOrderFile.Manifests;
using SilverModManager.Games.LoadOrderFile.Naming;

namespace SilverModManager.Games.LoadOrderFile.Tests;

public class SteamWorkshopModSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workshop-source-" + Guid.NewGuid().ToString("N"));

    private static readonly SteamWorkshopModSource Source = new(268500, new FileStemNamer(new Extension(".XComMod")));

    private static AbsolutePath Absolute(string path) => FileSystem.Shared.FromUnsanitizedFullPath(path);

    private string Game => Path.Combine(_root, "steamapps", "common", "XCOM 2");

    private void Subscribe(string id, params string[] files)
    {
        var folder = Path.Combine(_root, "steamapps", "workshop", "content", "268500", id);
        Directory.CreateDirectory(folder);
        foreach (var file in files) File.WriteAllText(Path.Combine(folder, file), "");
    }

    [Fact]
    public void EachSubscribedModIsNamedAfterItsDescriptorFile()
    {
        Directory.CreateDirectory(Game);
        Subscribe("200", "Second.XComMod", "Content.upk");
        Subscribe("100", "First.xcommod");

        Source.Find(Absolute(Game)).Should().Equal("First", "Second");
    }

    [Fact]
    public void AGameNotInAStoreLibraryHasNone()
    {
        var game = Path.Combine(_root, "games", "xcom2");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(Path.Combine(_root, "workshop", "content", "268500", "100"));
        File.WriteAllText(Path.Combine(_root, "workshop", "content", "268500", "100", "Stray.XComMod"), "");

        Source.Find(Absolute(game)).Should().BeEmpty();
    }

    [Fact]
    public void AnotherGamesWorkshopContentIsNotThisGames()
    {
        Directory.CreateDirectory(Game);
        var other = Path.Combine(_root, "steamapps", "workshop", "content", "1", "100");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "Other.XComMod"), "");

        Source.Find(Absolute(Game)).Should().BeEmpty();
    }

    [Fact]
    public void ALibraryWithNoWorkshopFolderHasNone()
    {
        Directory.CreateDirectory(Game);

        Source.Find(Absolute(Game)).Should().BeEmpty();
    }

    [Fact]
    public void ASubscriptionWithoutADescriptorFileIsNoMod()
    {
        Directory.CreateDirectory(Game);
        Subscribe("100", "Content.upk");

        Source.Find(Absolute(Game)).Should().BeEmpty();
    }

    [Fact]
    public void AnItemWithTwoDescriptorsStillGivesTwoNames()
    {
        Directory.CreateDirectory(Game);
        Subscribe("100", "Second.XComMod", "First.XComMod");

        Source.Find(Absolute(Game)).Should().Equal("First", "Second");
    }

    private void SubscribeWithAbout(string id, string packageId, string appId = "294100")
    {
        var about = Path.Combine(_root, "steamapps", "workshop", "content", appId, id, "About");
        Directory.CreateDirectory(about);
        File.WriteAllText(Path.Combine(about, "About.xml"), $"<ModMetaData><packageId>{packageId}</packageId></ModMetaData>");
    }

    [Fact]
    public void AManifestNamedItemIsListedByItsPackageId()
    {
        var game = Path.Combine(_root, "steamapps", "common", "RimWorld");
        Directory.CreateDirectory(game);
        SubscribeWithAbout("123", "Brrainz.Harmony");

        new SteamWorkshopModSource(294100, new ManifestNamer(new AboutXmlManifestReader())).Find(Absolute(game))
            .Should().Equal("brrainz.harmony");
    }

    [Fact]
    public void AnItemWithoutItsManifestIsSkipped()
    {
        var game = Path.Combine(_root, "steamapps", "common", "RimWorld");
        Directory.CreateDirectory(game);
        SubscribeWithAbout("123", "Brrainz.Harmony");
        Directory.CreateDirectory(Path.Combine(_root, "steamapps", "workshop", "content", "294100", "124"));

        new SteamWorkshopModSource(294100, new ManifestNamer(new AboutXmlManifestReader())).Find(Absolute(game))
            .Should().Equal("brrainz.harmony");
    }

    [Fact]
    public void TheListingPolicyDefaultsToListedOnly()
    {
        Source.Listing.Should().Be(ExternalListing.WhenListed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
