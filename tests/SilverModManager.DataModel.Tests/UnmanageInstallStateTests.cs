using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// UnManage reads the install's row after deleting its loadouts and clears it in a later commit. A rescan can land
/// in between, and must neither refuse UnManage's transaction nor leave disk state behind.
/// </summary>
public class UnmanageInstallStateTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<UnmanageInstallStateTests>(helper)
{
    [Fact]
    public async Task ClearInstallState_ARescanCommitsAfterTheRowWasRead_ClearsWhatTheRescanWroteToo()
    {
        EntityId row;
        using (var tx = Connection.BeginTransaction())
        {
            var r = new GameInstallMetadata.New(tx)
            {
                Store = GameStore.Steam,
                Path = "/not-a-folder/unmanaged",
                Game = GameId.From("Unmanaged"),
                Name = "Unmanaged",
            };
            var result = await tx.Commit();
            row = result.Remap(r).Id;
        }
        using (var tx = Connection.BeginTransaction())
        {
            NewEntry(tx, row, "before.txt");
            var txEntity = EntityId.From(tx.ThisTxId.Value);
            tx.Add(row, GameInstallMetadata.InitialDiskStateTransaction, txEntity);
            tx.Add(row, GameInstallMetadata.LastScannedDiskStateTransaction, txEntity);
            tx.Add(row, GameInstallMetadata.LastSyncedLoadoutTransaction, txEntity);
            await tx.Commit();
        }

        using var unmanage = Connection.BeginTransaction();
        SilverModManager.DataModel.LoadoutManager.ClearInstallState(unmanage, GameInstallMetadata.Load(Connection.Db, row));

        using (var rescan = Connection.BeginTransaction())
        {
            NewEntry(rescan, row, "after.txt");
            rescan.Add(row, GameInstallMetadata.LastScannedDiskStateTransaction, EntityId.From(rescan.ThisTxId.Value));
            await rescan.Commit();
        }

        await unmanage.Commit();

        var metadata = GameInstallMetadata.Load(Connection.Db, row);
        GameInstallMetadata.LastScannedDiskStateTransaction.Contains(metadata).Should().BeFalse();
        GameInstallMetadata.InitialDiskStateTransaction.Contains(metadata).Should().BeFalse();
        GameInstallMetadata.LastSyncedLoadoutTransaction.Contains(metadata).Should().BeFalse();
        DiskStateEntry.FindByGame(Connection.Db, metadata).Should().BeEmpty("the rescan's entries belong to the install that was unmanaged");
    }

    private static void NewEntry(ITransaction tx, EntityId row, string path)
        => _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
        {
            Path = new GamePath(LocationId.Game, path).ToGamePathParentTuple(row),
            Hash = Hash.From(0x1),
            Size = Size.From(1),
            LastModified = DateTimeOffset.UnixEpoch,
            GameId = row,
        };
}
