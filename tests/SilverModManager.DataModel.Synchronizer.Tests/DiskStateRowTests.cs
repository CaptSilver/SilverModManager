using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// The disk state rows an apply writes and removes: they belong to the install's one row, and removing one
/// removes what is stored, not what the sync tree remembered.
/// </summary>
public class DiskStateRowTests(ITestOutputHelper helper) : AStubbedGameTest<DiskStateRowTests>(helper)
{
    private static readonly GamePath Plain = new(LocationId.Game, "plain.txt");

    [Fact]
    public async Task DeletingAFileWhoseEntryChangedAfterTheTreeWasBuilt_RemovesTheWholeEntry()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)"plain.txt"], loadout.LoadoutId, "mod");
            await tx.Commit();
        }
        await Synchronizer.Synchronize(Refresh(loadout));

        using (var tx = Connection.BeginTransaction())
        {
            foreach (var mod in LoadoutItem.FindByLoadout(Connection.Db, loadout).Where(item => item.Name == "mod"))
                tx.Delete(mod.Id, recursive: true);
            await tx.Commit();
        }

        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);
        tree[Plain].Actions.Should().HaveFlag(Actions.DeleteFromDisk);
        var entry = tree[Plain].Disk.EntityId;

        // A rescan that lands between building the tree and applying it moves the entry past what the tree saw.
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(entry, DiskStateEntry.Hash, Hash.From(0xC4A6E));
            await tx.Commit();
        }

        await Synchronizer.RunActions(tree, Refresh(loadout));

        Connection.Db.Get(entry).Should().BeEmpty("a file that is gone from disk keeps no disk state");
    }

    [Fact]
    public async Task ApplyingALoadoutOnASecondRowForTheSameFolder_IsRefusedBeforeAnythingIsWritten()
    {
        var first = await CreateLoadout();
        var canonical = first.Installation;

        EntityId duplicate, stray;
        using (var tx = Connection.BeginTransaction())
        {
            var row = new GameInstallMetadata.New(tx)
            {
                Store = canonical.Store,
                Path = canonical.Path,
                Game = canonical.Game.Value,
                Name = canonical.Name,
            };
            var loadout = new Loadout.New(tx)
            {
                Name = "Stray",
                ShortName = "s",
                InstallationId = row,
                LoadoutKind = LoadoutKind.Default,
                Revision = 0,
                GameVersion = VanityVersion.From("Unknown"),
            };
            var result = await tx.Commit();
            (duplicate, stray) = (result.Remap(row).Id, result.Remap(loadout).Id);
        }
        var lastSyncedBefore = GameInstallMetadata.Load(Connection.Db, canonical.Id).LastSyncedLoadout.Id;

        var act = async () => await Synchronizer.Synchronize(Loadout.Load(Connection.Db, stray));

        await act.Should().ThrowAsync<SynchronizerPreflightException>();
        DiskStateEntry.FindByGame(Connection.Db, duplicate).Should().BeEmpty();
        GameInstallMetadata.LastSyncedLoadout.Contains(GameInstallMetadata.Load(Connection.Db, duplicate)).Should().BeFalse();
        GameInstallMetadata.Load(Connection.Db, canonical.Id).LastSyncedLoadout.Id.Should().Be(lastSyncedBefore);
    }
}
