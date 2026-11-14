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
        GameRegistry,
        ServiceProvider.GetRequiredService<IDeploymentBackendSelector>(),
        ServiceProvider.GetRequiredService<HeldMounts>(),
        Findings,
        NullLogger<MountLaunchGate>.Instance);

    private readonly MountRecoveryFindings Findings = new();

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

    [Fact]
    public async Task FinishWithoutASeenEndRecoversIntoTheMarkersLoadout()
    {
        var context = await ContextWith(mountingRoot: true) with { EndSeen = false };

        await NewGate().Finish(context);

        Mounting.Recoveries.Should().Equal((DataRoot, (LoadoutId?)context.LoadoutId));
    }

    [Fact]
    public async Task FinishWithoutASeenEndAndNoLoadoutRecoversWithoutATarget()
    {
        var context = await ContextWith(mountingRoot: true) with { EndSeen = false, LoadoutId = default };

        await NewGate().Finish(context);

        Mounting.Recoveries.Should().Equal((DataRoot, (LoadoutId?)null));
    }

    [Fact]
    public async Task FinishWithoutASeenEndLeavesARootThisProcessServesAlone()
    {
        var context = await ContextWith(mountingRoot: true);
        var gate = NewGate();
        await gate.Prepare(context, CancellationToken.None);

        // Another session's dead finish: the mount up now belongs to a run that is still going.
        await gate.Finish(context with { SessionId = Guid.NewGuid(), EndSeen = false });

        Mounting.Recoveries.Should().BeEmpty("a mount this process serves is not left over from anything");
        await gate.Finish(context);
    }

    [Fact]
    public async Task FinishAfterASeenEndWithNoScopeRecoversNothing()
    {
        var context = await ContextWith(mountingRoot: true);

        await NewGate().Finish(context);

        Mounting.Recoveries.Should().BeEmpty("a launch that mounted nothing has nothing to clean up");
    }

    [Fact]
    public async Task FinishWithoutASeenEndOnAPlanWithoutAMountingRootRecoversNothing()
    {
        var context = await ContextWith(mountingRoot: false) with { EndSeen = false };

        await NewGate().Finish(context);

        Mounting.Recoveries.Should().BeEmpty();
    }

    [Fact]
    public async Task ANeedsTargetReportIsKeptForTheHealthCheckUntilALaterRecoveryFindsNothing()
    {
        var context = await ContextWith(mountingRoot: true) with { EndSeen = false, LoadoutId = default };
        var id = GameRegistry.ForceGetMetadata(GameInstallation).GameInstallMetadataId;
        Mounting.RecoveryReports[DataRoot] = new MountRecoveryReport(MountRecoveryStatus.NeedsTarget, WasMounted: true, Staging: FileSystem.GetKnownPath(KnownPath.TempDirectory));
        var gate = NewGate();

        await gate.Finish(context);

        Findings.For(id).Should().ContainSingle().Which.Report.Status.Should().Be(MountRecoveryStatus.NeedsTarget);

        Mounting.RecoveryReports.Remove(DataRoot);
        await gate.Finish(context);

        Findings.For(id).Should().BeEmpty();
    }

    [Fact]
    public async Task ABlockedReportIsKeptForTheHealthCheck()
    {
        var context = await ContextWith(mountingRoot: true) with { EndSeen = false };
        var id = GameRegistry.ForceGetMetadata(GameInstallation).GameInstallMetadataId;
        Mounting.RecoveryReports[DataRoot] = new MountRecoveryReport(MountRecoveryStatus.Blocked, WasMounted: false, Recovery: FileSystem.GetKnownPath(KnownPath.TempDirectory));

        await NewGate().Finish(context);

        Findings.For(id).Should().ContainSingle().Which.Report.Status.Should().Be(MountRecoveryStatus.Blocked);
    }

    [Fact]
    public async Task AnOverCapReportIsKeptForTheHealthCheck()
    {
        var context = await ContextWith(mountingRoot: true) with { EndSeen = false };
        var id = GameRegistry.ForceGetMetadata(GameInstallation).GameInstallMetadataId;
        Mounting.RecoveryReports[DataRoot] = new MountRecoveryReport(MountRecoveryStatus.OverCap, WasMounted: false, Staging: FileSystem.GetKnownPath(KnownPath.TempDirectory));

        await NewGate().Finish(context);

        Findings.For(id).Should().ContainSingle().Which.Report.Status.Should().Be(MountRecoveryStatus.OverCap);
    }

    [Fact]
    public async Task AStillRunningReportIsNotKept()
    {
        var context = await ContextWith(mountingRoot: true) with { EndSeen = false };
        var id = GameRegistry.ForceGetMetadata(GameInstallation).GameInstallMetadataId;
        Mounting.RecoveryReports[DataRoot] = new MountRecoveryReport(MountRecoveryStatus.StillRunning, WasMounted: false);

        await NewGate().Finish(context);

        Findings.For(id).Should().BeEmpty();
    }

    [Fact]
    public async Task HasStaleMountSeesAMountOfTheDeclaredRootThatNothingHere_Holds()
    {
        var context = await ContextWith(mountingRoot: true);
        var gate = NewGate();
        gate.HasStaleMount(GameInstallation).Should().BeFalse();

        Mounting.Mounted = true;
        gate.HasStaleMount(GameInstallation).Should().BeTrue();

        await gate.Prepare(context, CancellationToken.None);
        gate.HasStaleMount(GameInstallation).Should().BeFalse("this process is serving that mount");
    }

    [Fact]
    public async Task HasStaleMountIgnoresAPlanWithoutAMountingRoot()
    {
        await ContextWith(mountingRoot: false);
        Mounting.Mounted = true;

        NewGate().HasStaleMount(GameInstallation).Should().BeFalse();
    }
}
