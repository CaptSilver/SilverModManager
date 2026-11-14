using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Games.TestFramework;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests.Launch;

/// <summary>
/// What a launch needs to know about the roots its plan serves through a mount, read from the recorded plan.
/// </summary>
public class LaunchMountsTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<LaunchMountsTests>(helper)
{
    private static readonly GamePath DataRoot = new(LocationId.Game, "Data");

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services).AddRecordingMountingDeploymentBackend();

    private async Task<LaunchContext> ContextWith(bool mountingRoot)
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        if (mountingRoot)
        {
            var id = GameRegistry.ForceGetMetadata(GameInstallation).GameInstallMetadataId;
            using var tx = Connection.BeginTransaction();
            ServiceProvider.GetRequiredService<IDeploymentPlanStore>().Write(tx, Connection.Db, id, DataRoot, RecordingMountingDeploymentBackend.BackendId, chosen: true);
            await tx.Commit();
        }
        var loadout = await CreateLoadout();
        return new LaunchContext(GameInstallation, loadout.LoadoutId, loadout.Name, []);
    }

    private ILaunchMounts Mounts => new DeploymentLaunchMounts(
        ServiceProvider.GetRequiredService<DeploymentLaunchRoots>(),
        ServiceProvider.GetRequiredService<IDeploymentPlanStore>(),
        ServiceProvider.GetServices<IDeploymentBackend>(),
        Connection);

    [Fact]
    public async Task AMountingRootIsListedWithItsBackendAndItsFolder()
    {
        var mounts = Mounts.For(await ContextWith(mountingRoot: true));

        var mounted = mounts.Mounted.Should().ContainSingle().Subject;
        mounted.Root.Should().Be(DataRoot);
        mounted.Backend.Should().Be(RecordingMountingDeploymentBackend.BackendId);
        mounted.Folder.Should().Be(GameInstallation.Locations.ToAbsolutePath(DataRoot));
    }

    [Fact]
    public async Task APlanWithoutAMountingRootListsNone()
    {
        var mounts = Mounts.For(await ContextWith(mountingRoot: false));

        mounts.Mounted.Should().BeEmpty();
    }

    [Fact]
    public async Task TheFilesystemRootsAreWhatTheRecordedPlansBackendsNeed()
    {
        var context = await ContextWith(mountingRoot: true);
        var expected = ServiceProvider.GetRequiredService<DeploymentLaunchRoots>()
            .For(GameInstallation, GameRegistry.ForceGetMetadata(GameInstallation).GameInstallMetadataId);

        Mounts.For(context).FilesystemRoots.Should().Equal(expected);
    }
}
