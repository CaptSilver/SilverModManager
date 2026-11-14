using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A mounting backend writes nothing under its root, so the rows it recorded stand in for the files on the disk side of
/// every sync tree.
/// </summary>
public class MountManifestOverlayTests(ITestOutputHelper helper) : AStubbedGameTest<MountManifestOverlayTests>(helper)
{
    private static readonly GamePath DataRoot = new(LocationId.Game, "Data");
    private static readonly GamePath ModEsp = DataRoot / "mod.esp";

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services).AddRecordingMountingDeploymentBackend();

    private RecordingMountingDeploymentBackend Mounting => ServiceProvider.GetRequiredService<RecordingMountingDeploymentBackend>();

    private GameInstallMetadataId MetadataId => GameRegistry.ForceGetMetadata(GameInstallation).Id;

    private async Task RouteDataToTheMountingBackend()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        using var tx = Connection.BeginTransaction();
        ServiceProvider.GetRequiredService<IDeploymentPlanStore>().Write(tx, Connection.Db, MetadataId, DataRoot, RecordingMountingDeploymentBackend.BackendId, chosen: true);
        await tx.Commit();
    }

    private async Task<(Loadout.ReadOnly Loadout, FileIdentity Identity, Size Size)> LoadoutWithAMod()
    {
        await RouteDataToTheMountingBackend();
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, "Mod");
        AddFile(tx, loadout.LoadoutId, group, ModEsp, "mod bytes", out var identity, out var size);
        // Without the bytes in the store the planner only warns that it cannot extract the file.
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(ModEsp.FileName, new MemoryStream("mod bytes"u8.ToArray())), identity, size)]);
        await tx.Commit();
        return (Refresh(loadout), identity, size);
    }

    private static MountManifestRow RowFor(GamePath path, FileIdentity identity, Size size) => new(path, identity, size, null, false, null);

    private ValueTask<bool> ShouldSynchronize(Loadout.ReadOnly loadout) =>
        Synchronizer.ShouldSynchronize(loadout, previousState: [], lastScannedState: []);

    [Fact]
    public async Task WithoutARowTheModFileIsPending()
    {
        var (loadout, _, _) = await LoadoutWithAMod();

        (await ShouldSynchronize(loadout)).Should().BeTrue();
    }

    [Fact]
    public async Task AManifestRowCountsAsDeployed()
    {
        var (loadout, identity, size) = await LoadoutWithAMod();
        Mounting.Views[DataRoot] = new MountManifestView(DataRoot, [RowFor(ModEsp, identity, size)]);

        (await ShouldSynchronize(loadout)).Should().BeFalse();
    }

    [Fact]
    public async Task ARemovedModsRowIsADeleteNode()
    {
        await RouteDataToTheMountingBackend();
        var loadout = await CreateLoadout();
        Mounting.Views[DataRoot] = new MountManifestView(DataRoot, [RowFor(ModEsp, FileIdentity.XxHash3(NexusMods.Hashing.xxHash3.Hash.From(1)), Size.FromLong(9))]);

        var tree = Synchronizer.BuildSyncTree(new List<PathPartPair>(), new List<PathPartPair>(), loadout);
        Synchronizer.ProcessSyncTree(tree);

        tree[ModEsp].Actions.Should().HaveFlag(Actions.DeleteFromDisk);
    }

    [Fact]
    public async Task TheIndexIsNeverOverlaid()
    {
        var (loadout, identity, size) = await LoadoutWithAMod();
        Mounting.Views[DataRoot] = new MountManifestView(DataRoot, [RowFor(ModEsp, identity, size)]);
        var stray = GameInstallation.Locations.ToAbsolutePath(DataRoot / "stray.esp");
        stray.Parent.CreateDirectory();
        await stray.WriteAllTextAsync("stray");

        var act = async () => await Synchronizer.ReindexState(GameInstallation);
        await act.Should().NotThrowAsync("a row whose file is not on disk is not a removed file");

        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);
        tree[DataRoot / "stray.esp"].Actions.Should().HaveFlag(Actions.IngestFromDisk);
    }

    [Fact]
    public async Task ASwitchSeesTheOldRowsAsDisk()
    {
        var (_, identity, size) = await LoadoutWithAMod();
        Mounting.Views[DataRoot] = new MountManifestView(DataRoot, [RowFor(ModEsp, identity, size)]);
        var second = await CreateLoadout();
        Mounting.Calls.Clear();

        await Synchronizer.BuildProcessRun(Refresh(second), GameInstallMetadata.Load(Connection.Db, MetadataId), CancellationToken.None);

        Mounting.Calls.Should().Contain((nameof(IDeploymentBackend.Delete), ModEsp));
    }
}
