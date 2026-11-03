using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Synchronizers.FolderCase;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The file index behind the collection page's search: which mods ship a file whose path contains some text.
/// </summary>
public class ModFileSearchIndexTests
{
    private static readonly EntityId Skyui = EntityId.From(0x1001);
    private static readonly EntityId Upscaler = EntityId.From(0x1002);
    private static readonly EntityId Group = EntityId.From(0x2001);

    private static ModFileSearchIndex Index(Dictionary<EntityId, EntityId>? parents = null, params (EntityId Owner, string Path)[] files) =>
        new(files.Select(f => new OwnedFilePath(f.Owner, f.Path)), owner => parents is not null && parents.TryGetValue(owner, out var parent) ? parent : null);

    [Fact]
    public void AModMatchesWhenAFilePathContainsTheText()
    {
        var index = Index(files: [(Skyui, "Data/Interface/skyui.swf"), (Upscaler, "Data/SKSE/Plugins/SkyrimUpscaler.dll")]);

        index.ItemsWithFileMatching("SkyrimUpscaler.dll").Should().BeEquivalentTo([Upscaler]);
        index.ItemsWithFileMatching("Data/").Should().BeEquivalentTo([Skyui, Upscaler]);
    }

    [Fact]
    public void ThePathMatchIgnoresCase()
    {
        var index = Index(files: [(Upscaler, "Data/SKSE/Plugins/UpscalerBasePlugin.dll")]);

        index.ItemsWithFileMatching("upscalerbaseplugin").Should().BeEquivalentTo([Upscaler]);
        index.ItemsWithFileMatching("UPSCALERBASEPLUGIN").Should().BeEquivalentTo([Upscaler]);
    }

    [Fact]
    public void TheGroupsAboveAMatchingModAreKept()
    {
        var index = Index(new Dictionary<EntityId, EntityId> { [Upscaler] = Group }, (Upscaler, "a/b.dll"));

        index.ItemsWithFileMatching("b.dll").Should().BeEquivalentTo([Upscaler, Group]);
    }

    [Fact]
    public void NoFileMatchesNothing()
    {
        var index = Index(files: [(Skyui, "a/b.dll")]);

        index.ItemsWithFileMatching("zzz").Should().BeEmpty();
        index.ItemsWithFileMatching("").Should().BeEmpty();
    }

    [Fact]
    public void TextSpanningTwoFilesDoesNotMatch()
    {
        var index = Index(files: [(Skyui, "a/one.dll"), (Skyui, "b/two.dll")]);

        index.ItemsWithFileMatching("one.dll\nb/two").Should().BeEmpty();
    }

    [Fact]
    public void AListOfAboutTwoHundredAndEightyThousandFilesIsSearchedInOneScanPerMod()
    {
        const int mods = 900;
        const int filesPerMod = 311;
        var files = new List<OwnedFilePath>(mods * filesPerMod);
        for (var mod = 0; mod < mods; mod++)
        for (var file = 0; file < filesPerMod; file++)
            files.Add(new OwnedFilePath(EntityId.From((ulong)(0x10_0000 + mod)), $"Data/Meshes/mod{mod}/part{file}.nif"));
        var needle = EntityId.From(0x10_0000 + 417);
        files.Add(new OwnedFilePath(needle, "Data/SKSE/Plugins/SkyrimUpscaler.dll"));
        var index = new ModFileSearchIndex(files, _ => null);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var found = index.ItemsWithFileMatching("skyrimupscaler.dll");
        started.Stop();

        found.Should().BeEquivalentTo([needle]);
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "a search is one substring scan per mod, not a pass over each file");
    }
}
