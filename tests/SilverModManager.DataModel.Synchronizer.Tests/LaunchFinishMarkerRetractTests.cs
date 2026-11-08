using FluentAssertions;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A sync that commits takes the pending-finish marker with it in the same transaction, so there is no window in
/// which the sync is recorded and the marker still says its ingest is owed.
/// </summary>
public class LaunchFinishMarkerRetractTests(ITestOutputHelper helper) : AStubbedGameTest<LaunchFinishMarkerRetractTests>(helper)
{
    private EntityId MetadataId => GameRegistry.ForceGetMetadata(GameInstallation).Id;

    private async Task WriteMarker(Loadout.ReadOnly loadout)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(MetadataId, GameInstallMetadata.LaunchFinishPending, loadout.Id);
        tx.Add(MetadataId, GameInstallMetadata.LaunchFinishPendingSession, Guid.NewGuid().ToString());
        tx.Add(MetadataId, GameInstallMetadata.LaunchFinishPendingGates, "SomeGate");
        tx.Add(MetadataId, GameInstallMetadata.LaunchFinishPendingGates, "OtherGate");
        await tx.Commit();
    }

    private static bool HasMarker(IDb db, EntityId id)
    {
        var metadata = GameInstallMetadata.Load(db, id);
        return metadata.Contains(GameInstallMetadata.LaunchFinishPending)
            || metadata.Contains(GameInstallMetadata.LaunchFinishPendingSession)
            || metadata.Contains(GameInstallMetadata.LaunchFinishPendingGates);
    }

    private List<Datom> TxDatoms(TxId tx) => Connection.AsOf(tx).Datoms(tx).Where(d => d.E == MetadataId).ToList();

    private bool IsAttribute(Datom d, IAttribute attribute) => d.A == Connection.Db.AttributeCache.GetAttributeId(attribute.Id);

    [Fact]
    public async Task ASyncRetractsTheMarkerInItsOwnCommit()
    {
        var loadout = await CreateLoadout();
        await WriteMarker(loadout);
        HasMarker(Connection.Db, MetadataId).Should().BeTrue("the marker was just written");
        var before = Connection.Db.BasisTxId;

        await SynchronizerService.Synchronize(loadout.LoadoutId);

        HasMarker(Connection.Db, MetadataId).Should().BeFalse();

        var retractTx = Enumerable.Range(1, (int)(Connection.Db.BasisTxId.Value - before.Value))
            .Select(i => TxId.From(before.Value + (ulong)i))
            .Where(t => TxDatoms(t).Any(d => d.IsRetract && IsAttribute(d, GameInstallMetadata.LaunchFinishPending)))
            .ToList();
        retractTx.Should().ContainSingle("the marker is retracted exactly once, by the sync");

        var t = retractTx.Single();
        TxDatoms(t).Should().Contain(d => !d.IsRetract && IsAttribute(d, GameInstallMetadata.LastSyncedLoadoutTransaction),
            "the retraction is in the commit that recorded the sync");
        HasMarker(Connection.AsOf(t), MetadataId).Should().BeFalse();
        HasMarker(Connection.AsOf(TxId.From(t.Value - 1)), MetadataId).Should().BeTrue("nothing before that commit touched it");
    }

    [Fact]
    public async Task ASyncWithoutAMarker_RetractsNoMarkerAttribute()
    {
        var loadout = await CreateLoadout();
        await SynchronizerService.Synchronize(loadout.LoadoutId);

        var syncTx = TxId.From(GameInstallMetadata.LastSyncedLoadoutTransactionId.Get(GameInstallMetadata.Load(Connection.Db, MetadataId)).Value);
        var datoms = TxDatoms(syncTx);

        datoms.Should().Contain(d => !d.IsRetract && IsAttribute(d, GameInstallMetadata.LastSyncedLoadoutTransaction),
            "this is the commit that recorded the sync");
        datoms.Should().NotContain(d => d.IsRetract && (IsAttribute(d, GameInstallMetadata.LaunchFinishPending)
            || IsAttribute(d, GameInstallMetadata.LaunchFinishPendingSession)
            || IsAttribute(d, GameInstallMetadata.LaunchFinishPendingGates)), "there was no marker to retract");
    }
}
