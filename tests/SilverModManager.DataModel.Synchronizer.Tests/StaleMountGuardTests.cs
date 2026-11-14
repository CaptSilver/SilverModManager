using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// While a root's mount from an earlier run is still there, nothing may apply, move the root, or index the folder:
/// each of those would treat what the mount serves as the game's own files.
/// </summary>
public class StaleMountGuardTests(ITestOutputHelper helper) : AStubbedGameTest<StaleMountGuardTests>(helper)
{
    private static readonly GamePath DataRoot = new(LocationId.Game, "Data");
    private const string Message = "Game/Data is still mounted from an earlier run; run silvermodmanager doctor";

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services).AddRecordingMountingDeploymentBackend();

    private RecordingMountingDeploymentBackend Mounting => ServiceProvider.GetRequiredService<RecordingMountingDeploymentBackend>();

    private async Task<Loadout.ReadOnly> LoadoutOnAMountingRoot(bool mounted)
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            ServiceProvider.GetRequiredService<IDeploymentPlanStore>().Write(tx, Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).Id, DataRoot, RecordingMountingDeploymentBackend.BackendId, chosen: true);
            await tx.Commit();
        }
        Mounting.Mounted = mounted;
        return Refresh(loadout);
    }

    [Fact]
    public async Task SynchronizeIsRefused()
    {
        var loadout = await LoadoutOnAMountingRoot(mounted: true);

        var act = async () => await Synchronizer.Synchronize(loadout);

        (await act.Should().ThrowAsync<ExecutableInUseException>()).Which.Message.Should().Be(Message);
    }

    [Fact]
    public async Task ChangingTheBackendIsRefused()
    {
        await LoadoutOnAMountingRoot(mounted: true);

        var act = async () => await SynchronizerService.ChangeDeploymentBackend(GameInstallation, new GamePath(LocationId.Game, RelativePath.Empty), DeploymentBackendId.Copy, chosen: true);

        (await act.Should().ThrowAsync<ExecutableInUseException>()).Which.Message.Should().Be(Message);
    }

    [Fact]
    public async Task ReindexingIsRefused()
    {
        await LoadoutOnAMountingRoot(mounted: true);

        var act = async () => await Synchronizer.ReindexState(GameInstallation);

        (await act.Should().ThrowAsync<ExecutableInUseException>()).Which.Message.Should().Be(Message);
    }

    [Fact]
    public async Task RescanningForExternalChangesIsRefused()
    {
        var loadout = await LoadoutOnAMountingRoot(mounted: true);

        var act = async () => await ((ALoadoutSynchronizer)Synchronizer).RescanExternalChanges(loadout);

        (await act.Should().ThrowAsync<ExecutableInUseException>()).Which.Message.Should().Be(Message);
    }

    [Fact]
    public async Task WithinSuspend_TheGuardLetsTheCallThrough()
    {
        await LoadoutOnAMountingRoot(mounted: true);

        using var _ = StaleMountGuard.Suspend();
        var act = async () => await Synchronizer.ReindexState(GameInstallation);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NothingMountedMeansNothingRefused()
    {
        var loadout = await LoadoutOnAMountingRoot(mounted: false);

        var act = async () => await Synchronizer.Synchronize(loadout);

        await act.Should().NotThrowAsync();
    }
}
