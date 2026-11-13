using FluentAssertions;
using NexusMods.Paths;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.DataModel.Undo;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// Setting a file aside: its Overrides row moves into a disabled, marked group, the file leaves the game folder, and
/// the loadout is applied so no later toggle or revert can turn the missing file into a recorded delete.
/// </summary>
public class SetAsideFilesTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<SetAsideFilesTests>(helper)
{
    private static readonly GamePath PathA = new(LocationId.Game, "bin/x64/plugins/a.dll");
    private static readonly GamePath PathB = new(LocationId.Game, "bin/x64/plugins/b.dll");

    // Under the test scratch root: a set aside trashes there rather than in the host's XDG_DATA_HOME.
    private readonly string _trashHome = TestScratch.Combine($"SetAsideFilesTests-trash-{Guid.NewGuid():N}");

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services).AddSingleton<ITrash>(new FreedesktopTrash(_trashHome));

    private ISetAsideFiles SetAsideFiles => ServiceProvider.GetRequiredService<ISetAsideFiles>();
    private UndoService Undo => ServiceProvider.GetRequiredService<UndoService>();
    private FakeGameLaunchSession LaunchSession => ServiceProvider.GetRequiredService<FakeGameLaunchSession>();

    private AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    /// <summary>Puts a file in the game folder and applies, so the loadout adopts it into Overrides the way a real one is.</summary>
    private async Task<Loadout.ReadOnly> LoadoutWithAdopted(params GamePath[] paths)
    {
        var loadout = await CreateLoadout();
        foreach (var path in paths)
        {
            OnDisk(path).Parent.CreateDirectory();
            await OnDisk(path).WriteAllTextAsync(path.Path.ToString());
        }
        await SynchronizerService.Synchronize(loadout.LoadoutId);
        return Refresh(loadout);
    }

    private LoadoutItemGroup.ReadOnly[] SetAsideGroups(Loadout.ReadOnly loadout) =>
        LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup().Where(g => g.IsSetAsideGroup).ToArray();

    private LoadoutItemWithTargetPath.ReadOnly[] RowsAt(Loadout.ReadOnly loadout, GamePath path) =>
        LoadoutItemWithTargetPath.FindByTargetPath(Connection.Db, path.ToGamePathParentTuple(loadout.Id))
            .Where(row => row.AsLoadoutItem().LoadoutId == loadout.LoadoutId).ToArray();

    private bool IsInOverrides(Loadout.ReadOnly loadout, GamePath path) =>
        RowsAt(loadout, path).Any(row => OverridesGroupLookup.IsInGroup(Connection.Db, row.Id, OverridesGroupLookup.Find(Refresh(loadout))));

    private LoadoutId? LastSynced() => GameInstallMetadata.LastSyncedLoadout.TryGetValue(
        GameInstallMetadata.Load(Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).Id), out var id) ? LoadoutId.From(id) : null;

    private TxId LastSyncedTx() => TxId.From(GameInstallMetadata.LastSyncedLoadoutTransaction.TryGetValue(
        GameInstallMetadata.Load(Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).Id), out var tx) ? tx.Value : 0UL);

    [Fact]
    public async Task CreatesTheGroupDisabledWithTheMarkerOnce()
    {
        var loadout = await LoadoutWithAdopted(PathA, PathB);

        var first = await SetAsideFiles.SetAside(loadout, [PathA], CancellationToken.None);
        var second = await SetAsideFiles.SetAside(Refresh(loadout), [PathB], CancellationToken.None);

        var group = SetAsideGroups(loadout).Should().ContainSingle("the second call finds the group by its marker").Subject;
        group.AsLoadoutItem().Name.Should().Be(ISetAsideFiles.GroupName);
        group.AsLoadoutItem().IsDisabled.Should().BeTrue();
        first.Group.Should().Be(second.Group);
        group.Children.OfTypeLoadoutItemWithTargetPath().Select(c => (GamePath)c.TargetPath).Should().BeEquivalentTo([PathA, PathB]);
        LoadoutItemGroupPriority.FindByTarget(Connection.Db, group.Id).Should().ContainSingle("the group takes part in conflicts like any mod");
    }

    [Fact]
    public async Task MovesOnlyOverridesRows()
    {
        var loadout = await LoadoutWithAdopted(PathA);
        var mod = await AddModGroup(loadout, "Mod", PathB);
        var before = Connection.Db.BasisTxId;

        var act = () => SetAsideFiles.SetAside(loadout, [PathA, PathB], CancellationToken.None);

        await act.Should().ThrowAsync<OverridesMoveRefusedException>().WithMessage("*b.dll*");
        Connection.Db.BasisTxId.Should().Be(before, "the refusal comes before the restore point and any write");
        IsInOverrides(loadout, PathA).Should().BeTrue();
        LoadoutItemGroup.Load(Connection.Db, mod).Children.OfTypeLoadoutItemWithTargetPath().Should().ContainSingle();
        OnDisk(PathA).FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task ADeleteMarkerIsRefused()
    {
        var loadout = await LoadoutWithAdopted();
        await AddOverridesDelete(loadout, PathA);
        var before = Connection.Db.BasisTxId;

        var act = () => SetAsideFiles.SetAside(Refresh(loadout), [PathA], CancellationToken.None);

        await act.Should().ThrowAsync<OverridesMoveRefusedException>().WithMessage("*a.dll*");
        Connection.Db.BasisTxId.Should().Be(before);
    }

    [Fact]
    public async Task WritesARestorePointBeforeMoving()
    {
        var loadout = await LoadoutWithAdopted(PathA);

        var result = await SetAsideFiles.SetAside(loadout, [PathA], CancellationToken.None);

        var revision = Undo.RevisionsFor(loadout.Id).Single(r => r.Revision.TxEntity.Value == result.RestorePoint.Value).Revision;
        var atRestorePoint = Connection.AsOf(TxId.From(revision.TxEntity.Value));
        LoadoutItemWithTargetPath.FindByTargetPath(atRestorePoint, PathA.ToGamePathParentTuple(loadout.Id)).Should().ContainSingle("the row was still in Overrides at the restore point");
        LoadoutItem.FindByLoadout(atRestorePoint, loadout.LoadoutId).OfTypeLoadoutItemGroup().Should().NotContain(g => g.IsSetAsideGroup);
        result.Moved.Should().Equal(PathA);
    }

    [Fact]
    public async Task TheFileIsOffDiskAndTheLoadoutIsApplied()
    {
        var loadout = await LoadoutWithAdopted(PathA);
        var syncedBefore = LastSyncedTx();

        var result = await SetAsideFiles.SetAside(loadout, [PathA], CancellationToken.None);

        result.Applied.Should().BeTrue();
        OnDisk(PathA).FileExists.Should().BeFalse();
        LastSyncedTx().Value.Should().BeGreaterThan(syncedBefore.Value, "the apply moved the last-synced transaction");
        (await Synchronizer.BuildSyncTree(Refresh(loadout))).Should().NotContainKey(PathA, "no side of the sync tree knows the file any more");
    }

    [Fact]
    public async Task EnablingTheGroupWithNoApplyInBetweenBringsTheFileBack()
    {
        var loadout = await LoadoutWithAdopted(PathA);
        var result = await SetAsideFiles.SetAside(loadout, [PathA], CancellationToken.None);

        await EnableItem(result.Group.Value);
        await SynchronizerService.Synchronize(loadout.LoadoutId);

        OnDisk(PathA).FileExists.Should().BeTrue();
        (await OnDisk(PathA).ReadAllTextAsync()).Should().Be(PathA.Path.ToString(), "the bytes come back from the store");
        RowsAt(loadout, PathA).Should().ContainSingle().Which.TryGetAsDeletedFile(out _).Should().BeFalse("no delete row outranks the group");
        IsInOverrides(loadout, PathA).Should().BeFalse();
    }

    [Fact]
    public async Task RevertingTheSetAsideBringsTheFileBack()
    {
        var loadout = await LoadoutWithAdopted(PathA);
        var result = await SetAsideFiles.SetAside(loadout, [PathA], CancellationToken.None);

        var revision = Undo.RevisionsFor(loadout.Id).Single(r => r.Revision.TxEntity.Value == result.RestorePoint.Value).Revision;
        await Undo.RevertTo(revision);
        await SynchronizerService.Synchronize(loadout.LoadoutId);

        OnDisk(PathA).FileExists.Should().BeTrue();
        RowsAt(loadout, PathA).Should().ContainSingle().Which.TryGetAsDeletedFile(out _).Should().BeFalse("the revert must not record a delete");
    }

    [Fact]
    public async Task ASetAsideProxyDllDoesNotComeBackUnderASnapshotBaseline()
    {
        // A baseline taken from a folder that already held a mod loader lists its files as looking like mods. Leaving
        // such a file on disk after the row moves would make the next sync re-adopt it into Overrides.
        await LooksLikeAMod(PathA);
        var loadout = await LoadoutWithAdopted(PathA);

        await SetAsideFiles.SetAside(loadout, [PathA], CancellationToken.None);

        OnDisk(PathA).FileExists.Should().BeFalse();
        IsInOverrides(loadout, PathA).Should().BeFalse("the file did not come back as a new external change");
        (await Synchronizer.BuildSyncTree(Refresh(loadout))).Should().NotContainKey(PathA);
    }

    [Fact]
    public async Task AnApplyRefusedWhileTheGameRunsIsReportedAndTheMoveStays()
    {
        var loadout = await LoadoutWithAdopted(PathA);
        LaunchSession.Running = true;

        var result = await SetAsideFiles.SetAside(loadout, [PathA], CancellationToken.None);

        result.Applied.Should().BeFalse();
        result.Moved.Should().Equal(PathA);
        OnDisk(PathA).FileExists.Should().BeFalse("the file is already off disk");
        SetAsideGroups(loadout).Single().Children.OfTypeLoadoutItemWithTargetPath().Should().ContainSingle();
    }

    private async Task LooksLikeAMod(GamePath path)
    {
        var store = ServiceProvider.GetRequiredService<IFileHashesService>().Local.Connection;
        var baseline = await LocalBaselineRows.AddLocalBaseline(store, GameInstallation.LocatorResult.Store,
            GameInstallation.Locations[LocationId.Game].Path.ToString(), LocalBaselineKind.Snapshot, []);
        using var tx = store.BeginTransaction();
        _ = new NonVanillaFile.New(tx)
        {
            BaselineId = baseline,
            Path = RelativePath.FromUnsanitizedInput(path.Path.ToString()),
            Reason = NonVanillaReason.LooksLikeAMod,
            Size = Size.From(1),
        };
        await tx.Commit();
    }

    private async Task<LoadoutItemGroupId> AddModGroup(Loadout.ReadOnly loadout, string name, GamePath path)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        AddFile(tx, loadout.LoadoutId, group, path);
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group].Value);
    }

    private async Task AddOverridesDelete(Loadout.ReadOnly loadout, GamePath path)
    {
        using (var create = Connection.BeginTransaction())
        {
            ((ALoadoutSynchronizer)Synchronizer).GetOrCreateOverridesGroup(create, loadout);
            await create.Commit();
        }
        var overrides = LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id).Single().Id;
        using var tx = Connection.BeginTransaction();
        OverridesGroupItems.AddReifiedDelete(tx, Refresh(loadout), LoadoutOverridesGroupId.From(overrides.Value), path);
        await tx.Commit();
    }
}
