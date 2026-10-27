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

    // An earlier build could leave an entry with its path but not its hash, size or time. Reading the disk
    // state must not lend it the next entry's values.
    [Fact]
    public async Task ReadingDiskState_AnEntryMissingItsHash_IsLeftOutAndTheNextEntryKeepsItsOwn()
    {
        EntityId row, whole;
        using (var tx = Connection.BeginTransaction())
        {
            var r = new GameInstallMetadata.New(tx)
            {
                Store = GameStore.Steam,
                Path = "/not-a-folder/half-written",
                Game = GameId.From("HalfWritten"),
                Name = "Half Written",
            };
            var result = await tx.Commit();
            row = result.Remap(r).Id;
        }
        using (var tx = Connection.BeginTransaction())
        {
            var half = tx.TempId(DiskStateEntry.EntryPartition);
            tx.Add(half, DiskStateEntry.Path, new GamePath(LocationId.Game, "half.txt").ToGamePathParentTuple(row));
            tx.Add(half, DiskStateEntry.Game, row);
            var w = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
            {
                Path = new GamePath(LocationId.Game, "whole.txt").ToGamePathParentTuple(row),
                Hash = Hash.From(0x2),
                Size = Size.From(2),
                LastModified = DateTimeOffset.UnixEpoch,
                GameId = row,
            };
            var result = await tx.Commit();
            whole = result.Remap(w).Id;
            result[half].Value.Should().BeLessThan(whole.Value, "the half entry has to come first in the merge join");
        }

        var pairs = Synchronizer.GetDiskStateForGame(GameInstallMetadata.Load(Connection.Db, row));

        pairs.Should().ContainSingle();
        pairs[0].Part.EntityId.Should().Be(whole);
        pairs[0].Path.Should().Be(new GamePath(LocationId.Game, "whole.txt"));
        pairs[0].Part.Hash.Should().Be(Hash.From(0x2));
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
