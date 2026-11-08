using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class LaunchFinishMarkersTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<LaunchFinishMarkersTests>(helper)
{
    private ILaunchFinishMarkers NewMarkers() => new LaunchFinishMarkers(Connection, GameRegistry, NullLogger<LaunchFinishMarkers>.Instance);

    private async Task<LoadoutId> ManagedLoadout()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        return (await CreateLoadout()).LoadoutId;
    }

    private EntityId MetadataId => GameRegistry.ForceGetMetadata(GameInstallation).Id;

    private async Task RecordASync(LoadoutId loadout)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(MetadataId, GameInstallMetadata.LastSyncedLoadout, loadout.Value);
        tx.Add(MetadataId, GameInstallMetadata.LastSyncedLoadoutTransaction, EntityId.From(tx.ThisTxId.Value));
        await tx.Commit();
    }

    [Fact]
    public async Task Write_ThenRead_ReturnsThePendingMarker()
    {
        var loadout = await ManagedLoadout();
        var markers = NewMarkers();
        var session = Guid.NewGuid();
        var basisBefore = Connection.Db.BasisTxId;

        await markers.Write(GameInstallation, loadout, session, ["a", "b"]);

        var markerDatoms = Connection.Db.Datoms(MetadataId)
            .Where(d => d.A == Connection.Db.AttributeCache.GetAttributeId(GameInstallMetadata.LaunchFinishPending.Id)
                        || d.A == Connection.Db.AttributeCache.GetAttributeId(GameInstallMetadata.LaunchFinishPendingSession.Id)
                        || d.A == Connection.Db.AttributeCache.GetAttributeId(GameInstallMetadata.LaunchFinishPendingGates.Id))
            .ToList();
        markerDatoms.Should().HaveCount(4);
        markerDatoms.Select(d => d.T).Distinct().Should().ContainSingle("Write is one transaction")
            .Which.Value.Should().Be(basisBefore.Value + 1);

        var marker = markers.Read(GameInstallation);
        marker.Should().NotBeNull();
        marker!.State.Should().Be(PendingLaunchFinishState.Pending);
        marker.LoadoutId.Should().Be(loadout);
        marker.LoadoutName.Should().Be(Loadout.Load(Connection.Db, loadout).Name);
        marker.SessionId.Should().Be(session);
        marker.GateNames.Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public async Task Read_WithoutAMarker_ReturnsNull()
    {
        await ManagedLoadout();

        NewMarkers().Read(GameInstallation).Should().BeNull();
    }

    [Fact]
    public async Task Read_WithoutMetadataForTheInstallation_ReturnsNull_AndRetractReturnsFalse()
    {
        var empty = Substitute.For<IGameRegistry>();
        GameInstallMetadata.ReadOnly none;
        empty.TryGetMetadata(Arg.Any<GameInstallation>(), out none).Returns(false);
        var markers = new LaunchFinishMarkers(Connection, empty, NullLogger<LaunchFinishMarkers>.Instance);

        markers.Read(GameInstallation).Should().BeNull();
        (await markers.Retract(GameInstallation)).Should().BeFalse();
    }

    [Fact]
    public async Task Read_UsesOneSnapshot_EvenWhenTheMarkerIsRetractedAfterTheMetadataWasTaken()
    {
        var loadout = await ManagedLoadout();
        var real = NewMarkers();
        await real.Write(GameInstallation, loadout, Guid.NewGuid(), ["a"]);
        await RecordASync(loadout);
        var stale = GameRegistry.ForceGetMetadata(GameInstallation);
        await real.Retract(GameInstallation);

        var registry = Substitute.For<IGameRegistry>();
        registry.TryGetMetadata(Arg.Any<GameInstallation>(), out Arg.Any<GameInstallMetadata.ReadOnly>())
            .Returns(x => { x[1] = stale; return true; });
        var markers = new LaunchFinishMarkers(Connection, registry, NullLogger<LaunchFinishMarkers>.Instance);

        markers.Read(GameInstallation)!.State.Should().Be(PendingLaunchFinishState.AlreadySynced);
    }

    [Fact]
    public async Task Read_AfterTheLoadoutWasLastSyncedBeforeTheMarker_IsStillPending()
    {
        var loadout = await ManagedLoadout();
        await RecordASync(loadout);
        var markers = NewMarkers();

        await markers.Write(GameInstallation, loadout, Guid.NewGuid(), ["a"]);

        markers.Read(GameInstallation)!.State.Should().Be(PendingLaunchFinishState.Pending);
    }

    [Fact]
    public async Task Read_AfterTheLoadoutWasSyncedAgain_IsAlreadySynced()
    {
        var loadout = await ManagedLoadout();
        var markers = NewMarkers();
        await markers.Write(GameInstallation, loadout, Guid.NewGuid(), ["a"]);

        await RecordASync(loadout);

        markers.Read(GameInstallation)!.State.Should().Be(PendingLaunchFinishState.AlreadySynced);
    }

    [Fact]
    public async Task Read_WhenAnotherLoadoutWasSyncedLast_IsAlreadySynced()
    {
        var loadout = await ManagedLoadout();
        var other = (await CreateLoadout()).LoadoutId;
        await RecordASync(loadout);
        var markers = NewMarkers();
        await markers.Write(GameInstallation, loadout, Guid.NewGuid(), ["a"]);

        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(MetadataId, GameInstallMetadata.LastSyncedLoadout, other.Value);
            await tx.Commit();
        }

        markers.Read(GameInstallation)!.State.Should().Be(PendingLaunchFinishState.AlreadySynced);
    }

    [Fact]
    public async Task Read_WhenTheLoadoutWasDeleted_IsLoadoutGone()
    {
        var loadout = await ManagedLoadout();
        var other = (await CreateLoadout()).LoadoutId;
        var markers = NewMarkers();
        await markers.Write(GameInstallation, loadout, Guid.NewGuid(), ["a"]);

        // Deleting the loadout that was synced last syncs it first, and that sync takes the marker with it. The
        // marker only outlives its loadout when another loadout is the one on disk.
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(MetadataId, GameInstallMetadata.LastSyncedLoadout, other.Value);
            await tx.Commit();
        }

        await DeleteLoadoutAsync(loadout);

        var marker = markers.Read(GameInstallation);
        marker.Should().NotBeNull();
        marker!.State.Should().Be(PendingLaunchFinishState.LoadoutGone);
        marker.LoadoutId.Should().Be(loadout);
        marker.LoadoutName.Should().BeNull();
    }

    [Fact]
    public async Task Read_WhenTheSyncedLoadoutWasDeleted_HasNoMarker()
    {
        var loadout = await ManagedLoadout();
        var markers = NewMarkers();
        await RecordASync(loadout);
        await markers.Write(GameInstallation, loadout, Guid.NewGuid(), ["a"]);

        await DeleteLoadoutAsync(loadout);

        markers.Read(GameInstallation).Should().BeNull("deleting the synced loadout syncs it first, which takes the pending finish");
    }

    [Fact]
    public async Task Retract_RemovesAllThreeAttributes_AndSaysWhetherOneWasThere()
    {
        var loadout = await ManagedLoadout();
        var markers = NewMarkers();
        await markers.Write(GameInstallation, loadout, Guid.NewGuid(), ["a", "b"]);

        (await markers.Retract(GameInstallation)).Should().BeTrue();

        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);
        GameInstallMetadata.LaunchFinishPending.TryGetValue(metadata, out _).Should().BeFalse();
        GameInstallMetadata.LaunchFinishPendingSession.TryGetValue(metadata, out _).Should().BeFalse();
        GameInstallMetadata.LaunchFinishPendingGates.Get(metadata).Should().BeEmpty();
        markers.Read(GameInstallation).Should().BeNull();

        var basis = Connection.Db.BasisTxId;
        (await markers.Retract(GameInstallation)).Should().BeFalse();
        Connection.Db.BasisTxId.Should().Be(basis, "retracting nothing must not commit an empty transaction");
    }
}
