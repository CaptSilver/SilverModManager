using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A file put back over a path the loadout recorded as deleted has to replace the delete marker
/// in the Overrides group, not sit on top of it where the winner query still reads it as deleted.
/// </summary>
public class RecreatedAfterDeleteTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<RecreatedAfterDeleteTests>(helper)
{
    private static readonly GamePath ModFilePath = new(LocationId.Game, "bin/x64/ThisIsATestFile.txt");

    private IEnumerable<LoadoutItemWithTargetPath.ReadOnly> OverridesRowsAt(Loadout.ReadOnly loadout)
    {
        return LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Where(r => r.TargetPath == ModFilePath && r.AsLoadoutItem().Parent.AsLoadoutItem().Name == "Overrides");
    }

    private async Task<Loadout.ReadOnly> SyncedLoadoutWithModFileDeleted()
    {
        var loadout = await CreateLoadout();
        var modFile = FileSystem.GetKnownPath(KnownPath.EntryDirectory) / "Resources" / "TestMod.zip";
        var libraryFile = await LibraryService.AddLocalFile(modFile);
        await LoadoutManager.InstallItem(libraryFile.AsLibraryFile().AsLibraryItem(), loadout);
        loadout = await Synchronizer.Synchronize(loadout.Rebase());

        loadout.InstallationInstance.Locations.ToAbsolutePath(ModFilePath).Delete();
        return await Synchronizer.Synchronize(loadout);
    }

    [Fact]
    public async Task AFileRecreatedOverARecordedDeleteReplacesTheDelete()
    {
        var loadout = await SyncedLoadoutWithModFileDeleted();
        OverridesRowsAt(loadout).Should().ContainSingle().Which.TryGetAsDeletedFile(out _).Should().BeTrue("the delete has to be recorded first");

        var disk = loadout.InstallationInstance.Locations.ToAbsolutePath(ModFilePath);
        await disk.WriteAllTextAsync("recreated");
        loadout = await Synchronizer.Synchronize(loadout);

        var row = OverridesRowsAt(loadout).Should().ContainSingle().Subject;
        row.TryGetAsDeletedFile(out _).Should().BeFalse("the file is back, so the delete marker has to go");
        row.TryGetAsLoadoutFile(out var file).Should().BeTrue();
        file.Hash.Should().Be("recreated".xxHash3AsUtf8());
        disk.FileExists.Should().BeTrue();
        (await disk.ReadAllTextAsync()).Should().Be("recreated");
    }

    [Fact]
    public async Task ARecreatedFileSettlesAfterOneSync()
    {
        var loadout = await SyncedLoadoutWithModFileDeleted();
        var disk = loadout.InstallationInstance.Locations.ToAbsolutePath(ModFilePath);
        await disk.WriteAllTextAsync("recreated");
        loadout = await Synchronizer.Synchronize(loadout);

        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);
        tree[ModFilePath].Actions.Should().Be(Actions.DoNothing, "disk, previous state and loadout all agree on the recreated file");

        var before = OverridesRowsAt(loadout).Single().Id;
        loadout = await Synchronizer.Synchronize(loadout);

        OverridesRowsAt(loadout).Should().ContainSingle().Which.Id.Should().Be(before);
        (await disk.ReadAllTextAsync()).Should().Be("recreated");
        OverridesRowsAt(loadout).Single().TryGetAsLoadoutFile(out _).Should().BeTrue();
    }

    [Fact]
    public async Task ADeleteOverAFileInTheOverridesStillRemovesTheFile()
    {
        var loadout = await CreateLoadout();
        var disk = loadout.InstallationInstance.Locations.ToAbsolutePath(ModFilePath);
        disk.Parent.CreateDirectory();
        await disk.WriteAllTextAsync("first");
        loadout = await Synchronizer.Synchronize(loadout);
        OverridesRowsAt(loadout).Should().ContainSingle().Which.TryGetAsLoadoutFile(out _).Should().BeTrue();

        disk.Delete();
        loadout = await Synchronizer.Synchronize(loadout);

        OverridesRowsAt(loadout).Should().BeEmpty();
    }
}
