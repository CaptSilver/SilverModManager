using System.Text;
using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Hashing.xxHash3;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.IO;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Tests;

/// <summary>
/// Every way the synchronizer can run actions against the game folder, checked against a snapshot of the
/// <c>Game</c> folder taken before: nothing in it is written, deleted, relinked or touched.
/// </summary>
public abstract class AGameFolderGuardTests<TTest>(ITestOutputHelper helper, bool dirty) : AEldenRingTest<TTest>(helper, dirty)
{
    private static readonly GamePath Me3ModFile = new(LocationId.Me3Mods, "Some Mod/parts/wp_a.dcx");

    private async Task AddModWithFiles(Loadout.ReadOnly loadout, string name, params GamePath[] paths)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        foreach (var path in paths)
        {
            var bytes = Encoding.UTF8.GetBytes($"{name}:{path.Path}");
            AddFile(tx, loadout.LoadoutId, group, path, $"{name}:{path.Path}");
            await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path.Path, new MemoryStream(bytes)), bytes.xxHash3(), Size.FromLong(bytes.Length))]);
        }

        await tx.Commit();
    }

    [Fact]
    public async Task ALoadoutFileInTheGameFolderMakesEverySyncThrowAndChangesNothing()
    {
        var loadout = await CreateLoadout();
        var before = SnapshotOfGameFolder();
        await AddModWithFiles(loadout, "Hook Mod", new GamePath(LocationId.Game, "Game/dinput8.dll"));

        var first = async () => await Synchronizer.Synchronize(Refresh(loadout));
        var second = async () => await Synchronizer.Synchronize(Refresh(loadout));

        var message = (await first.Should().ThrowAsync<SynchronizerPreflightException>()).Which.Message;
        message.Should().Contain("Game/dinput8.dll").And.Contain("Hook Mod").And.Contain("never written");
        await second.Should().ThrowAsync<SynchronizerPreflightException>("it keeps throwing until the mod is removed");
        SnapshotOfGameFolder().Should().Equal(before);
    }

    [Fact]
    public async Task SwitchingBetweenTwoLoadoutsLeavesTheGameFolderAlone()
    {
        var first = await CreateLoadout();
        await AddModWithFiles(first, "First Mod", Me3ModFile);
        await Synchronizer.Synchronize(Refresh(first));
        var second = await CreateLoadout();
        var before = SnapshotOfGameFolder();

        await Synchronizer.Synchronize(Refresh(second));
        await Synchronizer.Synchronize(Refresh(first));

        SnapshotOfGameFolder().Should().Equal(before);
    }

    [Fact]
    public async Task ActivatingALoadoutDirectlyLeavesTheGameFolderAlone()
    {
        var first = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(first));
        var second = await CreateLoadout();
        var before = SnapshotOfGameFolder();

        await LoadoutManager.ActivateLoadout(second.LoadoutId);

        SnapshotOfGameFolder().Should().Equal(before);
    }

    [Fact]
    public async Task DeactivatingTheCurrentLoadoutLeavesTheGameFolderAlone()
    {
        var loadout = await CreateLoadout();
        await AddModWithFiles(loadout, "Some Mod", Me3ModFile);
        await Synchronizer.Synchronize(Refresh(loadout));
        var before = SnapshotOfGameFolder();

        Synchronizer.EnsureBuildCovered(GameInstallation, GameInstallation.LocatorResult.LocatorIds.Distinct().ToArray());
        await LoadoutManager.DeactivateCurrentLoadout(GameInstallation);

        GameRegistry.ForceGetMetadata(GameInstallation).Contains(GameInstallMetadata.LastSyncedLoadout).Should().BeFalse("the reset ran");
        SnapshotOfGameFolder().Should().Equal(before);
    }

    [Fact]
    public async Task RemovingTheLastLoadoutLeavesTheGameFolderAlone()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));
        var before = SnapshotOfGameFolder();

        await DeleteLoadoutAsync(loadout.LoadoutId);

        GameRegistry.ForceGetMetadata(GameInstallation).Contains(GameInstallMetadata.LastSyncedLoadout).Should().BeFalse("the reset ran");
        SnapshotOfGameFolder().Should().Equal(before);
    }

    [Fact]
    public async Task CleaningTheGameFolderLeavesTheGameFolderAlone()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));
        var before = SnapshotOfGameFolder();

        await LoadoutManager.CleanGameFolder(Refresh(loadout));

        SnapshotOfGameFolder().Should().Equal(before);
    }

    [Fact]
    public async Task AFileDroppedBetweenTwoSyncsIsNotIngestedAndStaysWhereItIs()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));
        var dropped = RealGameFolder / "dropped.dll";
        await dropped.WriteAllTextAsync("dropped in between");
        var before = SnapshotOfGameFolder();

        await Synchronizer.Synchronize(Refresh(loadout));

        SnapshotOfGameFolder().Should().Equal(before);
        LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id)
            .SelectMany(g => g.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Should().BeEmpty();
    }
}

public class PristineGameFolderTests(ITestOutputHelper helper) : AGameFolderGuardTests<PristineGameFolderTests>(helper, dirty: false);

public class GameFolderLeftByAnotherModManagerTests(ITestOutputHelper helper) : AGameFolderGuardTests<GameFolderLeftByAnotherModManagerTests>(helper, dirty: true);
