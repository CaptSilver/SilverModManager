using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Games.TestFramework;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class PromoteStagedFilesActionHandlerTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<PromoteStagedFilesActionHandlerTests>(helper)
{
    private static readonly GamePath DataRoot = new(LocationId.Game, "Data");

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services).AddRecordingMountingDeploymentBackend();

    private RecordingMountingDeploymentBackend Mounting => ServiceProvider.GetRequiredService<RecordingMountingDeploymentBackend>();

    private readonly MountRecoveryFindings _findings = new();
    private readonly IDiagnosticManager _diagnostics = Substitute.For<IDiagnosticManager>();

    private PromoteStagedFilesActionHandler NewHandler() => new(
        Connection,
        ServiceProvider.GetRequiredService<IDeploymentPlanStore>(),
        ServiceProvider.GetServices<IDeploymentBackend>(),
        ServiceProvider.GetRequiredService<HeldMounts>(),
        _findings,
        _diagnostics);

    private GameInstallMetadataId InstallationId => GameRegistry.ForceGetMetadata(GameInstallation).GameInstallMetadataId;

    private async Task<(Loadout.ReadOnly Shown, Loadout.ReadOnly Chosen)> TwoLoadoutsOnAMountedPlan()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        using var tx = Connection.BeginTransaction();
        ServiceProvider.GetRequiredService<IDeploymentPlanStore>().Write(tx, Connection.Db, InstallationId, DataRoot, RecordingMountingDeploymentBackend.BackendId, chosen: true);
        await tx.Commit();
        var shown = await CreateLoadout();
        var chosen = await LoadoutManager.CreateLoadout(GameInstallation, "Chosen");
        return (shown, chosen);
    }

    private void Staged() =>
        _findings.Record(InstallationId, DataRoot, new MountRecoveryReport(MountRecoveryStatus.NeedsTarget, WasMounted: true, Staging: FileSystem.GetKnownPath(KnownPath.TempDirectory).Combine("staged")));

    [Fact]
    public async Task TheHandlerCallsRecoverWithTheChosenLoadout()
    {
        var (shown, chosen) = await TwoLoadoutsOnAMountedPlan();
        Staged();
        Mounting.RecoveryReports[DataRoot] = new MountRecoveryReport(MountRecoveryStatus.Promoted, WasMounted: false, PromotedFiles: 3);
        var action = DiagnosticActions.PromoteStagedFiles(InstallationId, "Game/Data", chosen.LoadoutId, chosen.Name);

        await NewHandler().ExecuteAsync(shown.LoadoutId, action.Arguments);

        Mounting.Recoveries.Should().ContainSingle().Which.Should().Be((DataRoot, (LoadoutId?)chosen.LoadoutId));
        _findings.For(InstallationId).Should().BeEmpty("a promoted root has nothing left to report");
        await _diagnostics.Received(1).RefreshAsync(shown.LoadoutId);
    }

    [Fact]
    public async Task ABlockedPromotionStaysAsAFinding()
    {
        var (shown, chosen) = await TwoLoadoutsOnAMountedPlan();
        Staged();
        var recovery = FileSystem.GetKnownPath(KnownPath.TempDirectory).Combine("recovery");
        Mounting.RecoveryReports[DataRoot] = new MountRecoveryReport(MountRecoveryStatus.Blocked, WasMounted: false, Recovery: recovery);
        var action = DiagnosticActions.PromoteStagedFiles(InstallationId, "Game/Data", chosen.LoadoutId, chosen.Name);

        await NewHandler().ExecuteAsync(shown.LoadoutId, action.Arguments);

        _findings.For(InstallationId).Should().ContainSingle().Which.Report.Status.Should().Be(MountRecoveryStatus.Blocked);
    }

    [Fact]
    public async Task AGameThatIsStillRunningRefusesAndKeepsTheFinding()
    {
        var (shown, chosen) = await TwoLoadoutsOnAMountedPlan();
        Staged();
        Mounting.RecoveryReports[DataRoot] = new MountRecoveryReport(MountRecoveryStatus.StillRunning, WasMounted: false);
        var action = DiagnosticActions.PromoteStagedFiles(InstallationId, "Game/Data", chosen.LoadoutId, chosen.Name);

        var act = () => NewHandler().ExecuteAsync(shown.LoadoutId, action.Arguments);

        await act.Should().ThrowAsync<ExecutableInUseException>();
        _findings.For(InstallationId).Should().ContainSingle().Which.Report.Status.Should().Be(MountRecoveryStatus.NeedsTarget);
    }

    [Fact]
    public async Task StagedFilesOverTheCapRefuseAndKeepTheFinding()
    {
        // Recording the over-cap report would replace the finding with one the Health Check does not show, so the
        // button would make the finding vanish while the files stay in staging.
        var (shown, chosen) = await TwoLoadoutsOnAMountedPlan();
        Staged();
        var staging = FileSystem.GetKnownPath(KnownPath.TempDirectory).Combine("staged");
        Mounting.RecoveryReports[DataRoot] = new MountRecoveryReport(MountRecoveryStatus.OverCap, WasMounted: false, Staging: staging);
        var action = DiagnosticActions.PromoteStagedFiles(InstallationId, "Game/Data", chosen.LoadoutId, chosen.Name);

        var act = () => NewHandler().ExecuteAsync(shown.LoadoutId, action.Arguments);

        (await act.Should().ThrowAsync<StagedFilesOverCapException>()).Which.Message.Should().Contain(staging.ToString());
        _findings.For(InstallationId).Should().ContainSingle().Which.Report.Status.Should().Be(MountRecoveryStatus.NeedsTarget);
    }

    [Fact]
    public async Task ARootThatIsNotInThePlanIsRefused()
    {
        var (shown, chosen) = await TwoLoadoutsOnAMountedPlan();
        var action = DiagnosticActions.PromoteStagedFiles(InstallationId, "Game/Other", chosen.LoadoutId, chosen.Name);

        var act = () => NewHandler().ExecuteAsync(shown.LoadoutId, action.Arguments);

        await act.Should().ThrowAsync<InvalidOperationException>();
        Mounting.Recoveries.Should().BeEmpty();
    }
}
