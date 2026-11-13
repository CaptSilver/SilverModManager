using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Games.LoadOrderFile.Manifests;
using SilverModManager.Games.LoadOrderFile.Naming;

namespace SilverModManager.Games.LoadOrderFile.Tests;

public class ModFolderModSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mod-folder-source-" + Guid.NewGuid().ToString("N"));

    private AbsolutePath Game => FileSystem.Shared.FromUnsanitizedFullPath(_root);

    private void Write(string relative, string content = "")
    {
        var file = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }

    private static string About(string id) => $"<ModMetaData><packageId>{id}</packageId></ModMetaData>";

    private static ModFolderModSource ByManifest(ExternalListing listing = ExternalListing.WhenListed)
        => new("Mods", new ManifestNamer(new AboutXmlManifestReader()), listing);

    [Fact]
    public void AFolderIsListedByItsManifestNotItsName()
    {
        Write("Mods/Harmony-2.3.6/About/About.xml", About("a.b"));

        ByManifest().Find(Game).Should().Equal("a.b");
    }

    [Fact]
    public void AFolderWithoutTheManifestIsNoMod()
    {
        Write("Mods/Loose/readme.txt");

        ByManifest().Find(Game).Should().BeEmpty();
    }

    [Fact]
    public void TheFolderNamerListsAFolderHoldingAPak()
    {
        Write("Mods/Cool/Data/a.pak");
        Write("Mods/Empty/readme.txt");

        new ModFolderModSource("Mods", new FolderNamer(new ModOrderTxtFormat()), ExternalListing.WhenListedOrNoFile).Find(Game)
            .Should().Equal("Cool");
    }

    [Fact]
    public void NamesComeInOrdinalFolderOrder()
    {
        Write("Mods/b/About/About.xml", About("b"));
        Write("Mods/A/About/About.xml", About("a"));
        Write("Mods/c/About/About.xml", About("c"));

        ByManifest().Find(Game).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void NoModFolderYieldsNothing()
    {
        ByManifest().Find(Game).Should().BeEmpty();
    }

    [Fact]
    public void TheListingIsTheOneTheSourceWasGiven()
    {
        ByManifest(ExternalListing.Always).Listing.Should().Be(ExternalListing.Always);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
