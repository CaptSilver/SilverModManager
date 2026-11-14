using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Games.TestFramework;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class MountLaunchGateTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<MountLaunchGateTests>(helper)
{
    private static readonly GamePath DataRoot = new(LocationId.Game, "Data");

    private sealed class SteerableSelector : IDeploymentBackendSelector
    {
        public DeploymentChoice Choose(GameInstallation installation, GamePath root) => new(DeploymentBackendId.Copy, "steered by the test");
    }

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services).AddRecordingMountingDeploymentBackend().AddSingleton<IDeploymentBackendSelector, SteerableSelector>();

    private RecordingMountingDeploymentBackend Mounting => ServiceProvider.GetRequiredService<RecordingMountingDeploymentBackend>();

    private MountLaunchGate NewGate() => new(
        ServiceProvider.GetRequiredService<IDeploymentMountScopeFactory>(),
        ServiceProvider.GetRequiredService<IDeploymentPlanStore>(),
        ServiceProvider.GetServices<IDeploymentBackend>(),
        Connection,
        ServiceProvider.GetRequiredService<IDeploymentBackendSelector>(),
        NullLogger<MountLaunchGate>.Instance);

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
        return new LaunchContext(GameInstallation, loadout.LoadoutId, loadout.Name, [], SessionId: Guid.NewGuid());
    }

    [Fact]
    public async Task CheckVetoesWithTheMoveCommandWhenPreflightRefuses()
    {
        var context = await ContextWith(mountingRoot: true);
        Mounting.Refusal = "the folder has gone";

        var veto = NewGate().Check(context);

        veto.Should().NotBeNull();
        veto!.Message.Should().StartWith($"Cannot start {GameInstallation.Game.DisplayName}: the folder has gone.");
        veto.Message.Should().Contain($"silvermodmanager deployment --game \"{GameInstallation.Game.DisplayName}\" --root Game/Data --use copy");
    }

    [Fact]
    public async Task CheckVetoesAForeignMount()
    {
        var context = await ContextWith(mountingRoot: true);
        Mounting.Mounted = true;

        var veto = NewGate().Check(context);

        veto!.Message.Should().Be("Game/Data is mounted by something else; run silvermodmanager doctor");
    }

    [Fact]
    public async Task CheckPassesAPlanWithoutAMountingRoot()
    {
        var context = await ContextWith(mountingRoot: false);
        Mounting.Refusal = "would refuse if asked";
        Mounting.Mounted = true;

        NewGate().Check(context).Should().BeNull();
    }

    [Fact]
    public async Task PrepareMountsTheMountingRoots()
    {
        var context = await ContextWith(mountingRoot: true);

        await NewGate().Prepare(context, CancellationToken.None);

        Mounting.Calls.Should().Contain(("Mount", DataRoot));
        ServiceProvider.GetRequiredService<HeldMounts>().Any.Should().BeTrue();
    }

    [Fact]
    public async Task PrepareCreatesNoScopeForAPlanWithoutAMountingRoot()
    {
        var context = await ContextWith(mountingRoot: false);
        var gate = NewGate();

        await gate.Prepare(context, CancellationToken.None);
        await gate.Finish(context);

        Mounting.Calls.Should().NotContain(c => c.Phase == "Mount" || c.Phase == "Finish");
    }

    [Fact]
    public async Task FinishFinishesTheScopeOfItsSession()
    {
        var context = await ContextWith(mountingRoot: true);
        var gate = NewGate();
        await gate.Prepare(context, CancellationToken.None);

        await gate.Finish(context);

        Mounting.Calls.Should().Contain(("Finish", DataRoot));
        ServiceProvider.GetRequiredService<HeldMounts>().Any.Should().BeFalse();
    }

    [Fact]
    public async Task FinishWithAMountStillInUseThrowsAnExceptionNamingTheGameAndTheRoot()
    {
        var context = await ContextWith(mountingRoot: true);
        Mounting.FinishResults[DataRoot] = new MountFinishResult(MountFinishStatus.StillInUse, []);
        var gate = NewGate();
        await gate.Prepare(context, CancellationToken.None);

        var act = () => gate.Finish(context);

        var thrown = (await act.Should().ThrowAsync<MountStillInUseException>()).Which;
        thrown.Message.Should().Be($"{GameInstallation.Game.DisplayName} still has files open under Game/Data; its changes are promoted the next time the app applies or launches");
    }

    [Fact]
    public async Task AFinishForAnotherSessionLeavesTheScopeAlone()
    {
        var context = await ContextWith(mountingRoot: true);
        var gate = NewGate();
        await gate.Prepare(context, CancellationToken.None);

        await gate.Finish(context with { SessionId = Guid.NewGuid() });

        Mounting.Calls.Should().NotContain(c => c.Phase == "Finish");
        await gate.Finish(context);
    }
}
