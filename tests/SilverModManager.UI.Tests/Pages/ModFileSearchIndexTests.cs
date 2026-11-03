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

        index.ItemsWithFileMatching("SkyrimUpscaler.dll", default).Should().BeEquivalentTo([Upscaler]);
        index.ItemsWithFileMatching("Data/", default).Should().BeEquivalentTo([Skyui, Upscaler]);
    }

    [Fact]
    public void ThePathMatchIgnoresCase()
    {
        var index = Index(files: [(Upscaler, "Data/SKSE/Plugins/UpscalerBasePlugin.dll")]);

        index.ItemsWithFileMatching("upscalerbaseplugin", default).Should().BeEquivalentTo([Upscaler]);
        index.ItemsWithFileMatching("UPSCALERBASEPLUGIN", default).Should().BeEquivalentTo([Upscaler]);
    }

    [Fact]
    public void TheGroupsAboveAMatchingModAreKept()
    {
        var index = Index(new Dictionary<EntityId, EntityId> { [Upscaler] = Group }, (Upscaler, "a/b.dll"));

        index.ItemsWithFileMatching("b.dll", default).Should().BeEquivalentTo([Upscaler, Group]);
    }

    [Fact]
    public void NoFileMatchesNothing()
    {
        var index = Index(files: [(Skyui, "a/b.dll")]);

        index.ItemsWithFileMatching("zzz", default).Should().BeEmpty();
        index.ItemsWithFileMatching("", default).Should().BeEmpty();
    }

    [Fact]
    public void TextSpanningTwoFilesDoesNotMatch()
    {
        var index = Index(files: [(Skyui, "a/one.dll"), (Skyui, "b/two.dll")]);

        index.ItemsWithFileMatching("one.dll\nb/two", default).Should().BeEmpty();
    }

    [Fact]
    public void ASearchThatIsCancelledStopsScanning()
    {
        var index = Index(files: [(Skyui, "a/b.dll")]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var act = () => index.ItemsWithFileMatching("b.dll", cancelled.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    private static List<OwnedFilePath> ManyFiles(int mods, int filesPerMod, EntityId needle)
    {
        var files = new List<OwnedFilePath>(mods * filesPerMod + 1);
        for (var mod = 0; mod < mods; mod++)
        for (var file = 0; file < filesPerMod; file++)
            files.Add(new OwnedFilePath(EntityId.From((ulong)(0x10_0000 + mod)), $"Data/Meshes/mod{mod}/part{file}.nif"));
        files.Add(new OwnedFilePath(needle, "Data/SKSE/Plugins/SkyrimUpscaler.dll"));
        return files;
    }

    [Fact]
    public void AListOfAboutTwoHundredAndEightyThousandFilesIsKeptAsOneTextPerMod()
    {
        var needle = EntityId.From(0x10_0000 + 417);
        var index = new ModFileSearchIndex(ManyFiles(mods: 900, filesPerMod: 311, needle), _ => null);

        index.OwnerCount.Should().Be(900, "a search scans one text per mod, not one string per file");
        index.ItemsWithFileMatching("skyrimupscaler.dll", default).Should().BeEquivalentTo([needle]);
    }

    [Fact]
    public void BuildingTheIndexOfAboutTwoHundredAndEightyThousandFilesAllocatesAFewTimesTheirText()
    {
        var needle = EntityId.From(0x10_0000 + 417);
        var files = ManyFiles(mods: 900, filesPerMod: 311, needle);
        var characters = files.Sum(file => (long)file.Path.Length + 1);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var index = new ModFileSearchIndex(files, _ => null);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        index.OwnerCount.Should().Be(900);
        // Chars are two bytes. Gathering the text and turning it into a string costs a small multiple of it; a
        // per-file object or string would push the total well past this.
        allocated.Should().BeLessThan(characters * 2 * 4, "the build copies each path once into its mod's text");
    }
}
