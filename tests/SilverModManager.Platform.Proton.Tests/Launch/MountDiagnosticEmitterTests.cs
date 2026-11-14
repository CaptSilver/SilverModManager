using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Games.TestFramework;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class MountDiagnosticEmitterTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<MountDiagnosticEmitterTests>(helper)
{
    private static readonly GamePath DataRoot = new(LocationId.Game, "Data");

    private sealed class SteerableSelector : IDeploymentBackendSelector
    {
        public DeploymentChoice Choose(GameInstallation installation, GamePath root) => new(DeploymentBackendId.Copy, "steered by the test");
    }

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services).AddRecordingMountingDeploymentBackend().AddSingleton<IDeploymentBackendSelector, SteerableSelector>();

    private RecordingMountingDeploymentBackend Mounting => ServiceProvider.GetRequiredService<RecordingMountingDeploymentBackend>();

    private readonly MountRecoveryFindings _findings = new();

    private MountDiagnosticEmitter NewEmitter() => new(
        ServiceProvider.GetRequiredService<IDeploymentPlanStore>(),
        ServiceProvider.GetServices<IDeploymentBackend>(),
        _findings,
        ServiceProvider.GetRequiredService<IDeploymentBackendSelector>());

    private GameInstallMetadataId InstallationId => GameRegistry.ForceGetMetadata(GameInstallation).GameInstallMetadataId;

    private async Task<Loadout.ReadOnly> LoadoutWith(bool mountingRoot)
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        if (mountingRoot)
        {
            using var tx = Connection.BeginTransaction();
            ServiceProvider.GetRequiredService<IDeploymentPlanStore>().Write(tx, Connection.Db, InstallationId, DataRoot, RecordingMountingDeploymentBackend.BackendId, chosen: true);
            await tx.Commit();
        }
        return await CreateLoadout();
    }

    private static async Task<List<Diagnostic>> Collect(MountDiagnosticEmitter emitter, Loadout.ReadOnly loadout)
    {
        var found = new List<Diagnostic>();
        await foreach (var diagnostic in emitter.Diagnose(loadout, CancellationToken.None)) found.Add(diagnostic);
        return found;
    }

    private static string Summary(Diagnostic d) => d.FormatSummary(new PlainDiagnosticWriter());

    private static string Details(Diagnostic d) => d.FormatDetails(new PlainDiagnosticWriter());

    [Fact]
    public async Task EmitsFuseUnavailableForAFuseRootWhenPreflightRefuses()
    {
        var loadout = await LoadoutWith(mountingRoot: true);
        Mounting.Refusal = "/dev/fuse does not exist";

        var found = await Collect(NewEmitter(), loadout);

        var diagnostic = found.Should().ContainSingle().Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        // The preflight refuses for reasons other than FUSE itself (a missing folder, a file the game writes under the
        // root), so the summary states the reason rather than blaming FUSE.
        Summary(diagnostic).Should().Be("Game/Data cannot be mounted: /dev/fuse does not exist");
        Details(diagnostic).Should().Contain($"silvermodmanager deployment --game \"{GameInstallation.Game.DisplayName}\" --root Game/Data --use copy");
    }

    [Fact]
    public async Task EmitsNothingForAFuseRootThatPreflightAccepts()
    {
        var loadout = await LoadoutWith(mountingRoot: true);

        (await Collect(NewEmitter(), loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task EmitsNothingWhileTheRootIsMounted()
    {
        // The preflight refuses a mounted root, but that is a run holding it, not a machine without FUSE.
        var loadout = await LoadoutWith(mountingRoot: true);
        Mounting.Refusal = "Game/Data is already mounted";
        Mounting.Mounted = true;

        (await Collect(NewEmitter(), loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task EmitsNeedsLoadoutWithOneActionPerLoadout()
    {
        var first = await LoadoutWith(mountingRoot: true);
        var second = await LoadoutManager.CreateLoadout(GameInstallation, "Second");
        var staging = FileSystem.GetKnownPath(KnownPath.TempDirectory).Combine("staged");
        _findings.Record(InstallationId, DataRoot, new MountRecoveryReport(MountRecoveryStatus.NeedsTarget, WasMounted: true, Staging: staging));

        // The diagnostic manager hands the emitter a loadout read from the current database, which has both.
        var found = await Collect(NewEmitter(), Loadout.Load(Connection.Db, first.LoadoutId));

        var diagnostic = found.Should().ContainSingle().Subject;
        Summary(diagnostic).Should().Be($"Files the game wrote under Game/Data are waiting in {staging}; choose the loadout they belong to");
        diagnostic.Actions.Should().HaveCount(2).And.OnlyContain(a => a.Id == DiagnosticActions.PromoteStagedFilesId);
        diagnostic.Actions.Select(a => a.Label).Should().BeEquivalentTo(["Promote into " + first.Name, "Promote into Second"]);
        var chosen = diagnostic.Actions.Select(a => DiagnosticActions.ParsePromoteStagedFiles(a.Arguments)).ToArray();
        chosen.Should().OnlyContain(c => c.Installation == InstallationId && c.Root == "Game/Data");
        chosen.Select(c => c.Loadout).Should().BeEquivalentTo([first.LoadoutId, second.LoadoutId]);
    }

    [Fact]
    public async Task EmitsBlockedNamingTheRecoveryFolder()
    {
        var loadout = await LoadoutWith(mountingRoot: true);
        var recovery = FileSystem.GetKnownPath(KnownPath.TempDirectory).Combine("recovery");
        _findings.Record(InstallationId, DataRoot, new MountRecoveryReport(MountRecoveryStatus.Blocked, WasMounted: false, Recovery: recovery));

        var found = await Collect(NewEmitter(), loadout);

        var diagnostic = found.Should().ContainSingle().Subject;
        Summary(diagnostic).Should().Be($"Files the game wrote under Game/Data could not be promoted; they are in {recovery}");
        diagnostic.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task EmitsNothingForAPlanWithoutAFuseRoot()
    {
        var loadout = await LoadoutWith(mountingRoot: false);
        Mounting.Refusal = "would refuse if asked";

        (await Collect(NewEmitter(), loadout)).Should().BeEmpty();
    }
}
