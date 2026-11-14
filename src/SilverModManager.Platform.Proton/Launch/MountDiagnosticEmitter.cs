using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// Reports what is wrong with a game's mounted folders: a machine that cannot mount them, and what a session that
/// ended badly left behind in staging.
/// </summary>
/// <remarks>
/// The reasons come from the backend's own preflight rather than a FUSE probe of this emitter's, so the finding says
/// what a launch would say. A root that is mounted right now is skipped: the preflight refuses it too, but that is a
/// run holding the folder, not a machine without FUSE. Findings about a dead session's files are those the last
/// recovery recorded in <see cref="MountRecoveryFindings"/>; nothing here runs a recovery.
/// </remarks>
public sealed class MountDiagnosticEmitter(
    IDeploymentPlanStore plans,
    IEnumerable<IDeploymentBackend> backends,
    MountRecoveryFindings findings,
    IDeploymentBackendSelector selector) : ILoadoutDiagnosticEmitter
{
    private readonly Dictionary<DeploymentBackendId, IMountingDeploymentBackend> _mounting = backends.OfType<IMountingDeploymentBackend>().ToDictionary(static b => b.Id);

    /// <inheritdoc />
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => DiagnoseAsync(loadout, cancellationToken);

    private async IAsyncEnumerable<Diagnostic> DiagnoseAsync(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var installation = loadout.InstallationInstance;
        var gameName = installation.Game.DisplayName;

        foreach (var row in plans.Load(loadout.Db, loadout.InstallationId).Roots)
        {
            if (!_mounting.TryGetValue(row.Backend, out var backend)) continue;
            if (backend.IsMounted(installation, row.Root)) continue;

            var preflight = backend.Preflight(installation, row.Root);
            if (preflight.Accepted) continue;

            var pick = selector.Choose(installation, row.Root).Backend.Value;
            yield return MountDiagnostics.CreateFuseUnavailableForMountedRoot(gameName, DeploymentRootNames.Describe(row.Root), preflight.Reason, pick);
        }

        foreach (var (root, report) in findings.For(loadout.InstallationId))
        {
            var described = DeploymentRootNames.Describe(root);
            switch (report.Status)
            {
                case MountRecoveryStatus.Blocked when report.Recovery is { } recovery:
                    yield return MountDiagnostics.CreateMountRecoveryBlocked(gameName, described, recovery.ToString());
                    break;
                case MountRecoveryStatus.NeedsTarget when report.Staging is { } staging:
                    yield return MountDiagnostics.CreateMountRecoveryNeedsLoadout(gameName, described, staging.ToString())
                        with { Actions = [..PromotionActions(loadout, described)] };
                    break;
            }
        }
    }

    // One action per loadout of the game, since recovery will not choose for the person.
    private static IEnumerable<DiagnosticAction> PromotionActions(Loadout.ReadOnly shown, string root) =>
        Loadout.All(shown.Db)
            .Where(l => l.InstallationId == shown.InstallationId && l.IsVisible())
            .OrderBy(static l => l.Name, StringComparer.Ordinal)
            .Select(l => DiagnosticActions.PromoteStagedFiles(l.InstallationId, root, l.LoadoutId, l.Name));
}
