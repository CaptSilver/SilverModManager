using System.Collections.Immutable;
using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// Promotes the files a dead mounted session left in staging into the loadout the person picked, by running the
/// root's recovery with that loadout as its target. The button names the loadout, so nothing is asked again.
/// </summary>
[UsedImplicitly]
public sealed class PromoteStagedFilesActionHandler(
    IConnection connection,
    IDeploymentPlanStore plans,
    IEnumerable<IDeploymentBackend> backends,
    HeldMounts held,
    MountRecoveryFindings findings,
    IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    private readonly Dictionary<DeploymentBackendId, IMountingDeploymentBackend> _mounting = backends.OfType<IMountingDeploymentBackend>().ToDictionary(static b => b.Id);

    /// <inheritdoc />
    public string ActionId => DiagnosticActions.PromoteStagedFilesId;

    /// <inheritdoc />
    public bool RunsUnattended => true;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        var (installationId, rootName, target) = DiagnosticActions.ParsePromoteStagedFiles(arguments);

        var shown = Loadout.Load(connection.Db, loadoutId);
        if (!shown.IsValid()) throw new InvalidOperationException($"The loadout {loadoutId} no longer exists.");
        if (shown.InstallationId != installationId) throw new InvalidOperationException("The staged files belong to another installation than the loadout that offered the action.");

        var chosen = Loadout.Load(connection.Db, target);
        if (!chosen.IsValid() || chosen.InstallationId != installationId) throw new InvalidOperationException($"The loadout {target} no longer exists.");

        var rows = plans.Load(connection.Db, installationId).Roots.Where(r => DeploymentRootNames.Describe(r.Root) == rootName).ToArray();
        if (rows.Length == 0) throw new InvalidOperationException($"{rootName} is not a deployed root of {shown.InstallationInstance.Game.DisplayName} any more.");
        var row = rows[0];
        if (!_mounting.TryGetValue(row.Backend, out var backend))
            throw new InvalidOperationException($"{rootName} is deployed with {row.Backend}, which keeps nothing in staging.");

        var installation = shown.InstallationInstance;
        var game = installation.Game.DisplayName;
        if (held.Current.Any(h => h.Root == row.Root && h.Installation.Equals(installation)))
            throw new ExecutableInUseException($"{game} is running from {rootName} right now. Its files are promoted when it quits.");

        var report = await backend.Recover(installation, row.Root, target, CancellationToken.None);
        if (report.Status == MountRecoveryStatus.StillRunning)
            throw new ExecutableInUseException($"{game} is still running. Quit it first, then promote the files.");
        // Not recorded: the Health Check has no finding for an over-cap report, so recording it would drop the one
        // the person just clicked while the files stay where they are.
        if (report.Status == MountRecoveryStatus.OverCap)
            throw new StagedFilesOverCapException(
                $"The files {game} wrote under {rootName} add up to more than the promotion size cap, so none were promoted. They are still in {report.Staging?.ToString() ?? "staging"}.");

        findings.Record(installationId, row.Root, report);
        await diagnosticManager.RefreshAsync(loadoutId);
    }
}
