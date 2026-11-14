using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// Mounts the roots a loadout serves through a mount once the launch has applied it, and finishes them before the
/// launch takes the game folder's changes back in. A launch of a plan with no mounting root passes through untouched.
/// </summary>
public sealed class MountLaunchGate(
    IDeploymentMountScopeFactory scopes,
    IDeploymentPlanStore plans,
    IEnumerable<IDeploymentBackend> backends,
    IConnection connection,
    IGameRegistry registry,
    IDeploymentBackendSelector selector,
    HeldMounts held,
    MountRecoveryFindings findings,
    ILogger<MountLaunchGate> logger) : ILaunchGate, IStaleMountSource
{
    private readonly Dictionary<DeploymentBackendId, IMountingDeploymentBackend> _mounting = backends.OfType<IMountingDeploymentBackend>().ToDictionary(static b => b.Id);

    // One scope per launch, found again by the session id the launch carries from its check to its finish.
    private readonly ConcurrentDictionary<Guid, IDeploymentMountScope> _scopes = new();

    /// <inheritdoc />
    public Veto? Check(LaunchContext context)
    {
        var game = context.Installation.Game.DisplayName;
        foreach (var (root, backend) in MountingRoots(context))
        {
            var described = DeploymentRootNames.Describe(root);

            // Before the preflight, which also refuses a mounted root but could only say it is "already mounted".
            if (backend.IsMounted(context.Installation, root))
                return new Veto($"{described} is mounted by something else; run silvermodmanager doctor");

            var preflight = backend.Preflight(context.Installation, root);
            if (!preflight.Accepted)
            {
                var pick = selector.Choose(context.Installation, root).Backend.Value;
                return new Veto($"Cannot start {game}: {preflight.Reason}. Run 'silvermodmanager deployment --game \"{game}\" --root {described} --use {pick}' to move it.");
            }
        }
        return null;
    }

    /// <inheritdoc />
    public async Task Prepare(LaunchContext context, CancellationToken cancellationToken)
    {
        if (!MountingRoots(context).Any()) return;

        var scope = scopes.Create();
        try
        {
            await scope.Mount(context.Installation, Loadout.Load(connection.Db, context.LoadoutId), cancellationToken);
        }
        catch
        {
            // The scope finished whatever it had mounted before it threw.
            await scope.DisposeAsync();
            throw;
        }
        _scopes[context.SessionId] = scope;
    }

    /// <inheritdoc />
    public async Task Finish(LaunchContext context)
    {
        if (!_scopes.TryRemove(context.SessionId, out var scope))
        {
            // No scope here and the end was not watched: the session that mounted died, so what it left is cleaned up.
            if (!context.EndSeen) await RecoverLeftovers(context);
            return;
        }

        await using (scope)
        {
            var busy = await scope.FinishNamingBusyRoots(CancellationToken.None);
            if (busy.Count == 0) return;

            var game = context.Installation.Game.DisplayName;
            logger.LogWarning("{Game} left files open under {Roots} after it exited", game, busy);
            throw MountStillInUseException.For(game, busy);
        }
    }

    /// <inheritdoc />
    public bool HasStaleMount(GameInstallation installation) =>
        MountingRoots(installation).Any(r => !ServedHere(installation, r.Root) && r.Backend.IsMounted(installation, r.Root));

    // A root a run in this process still holds (a tool, or another launch) is live whatever else has died.
    private bool ServedHere(GameInstallation installation, GamePath root) =>
        held.Current.Any(h => h.Root == root && h.Installation.Equals(installation));

    // The target is the loadout the run served. A finish with no marker has none, and recovery asks for one rather than guessing.
    private async Task RecoverLeftovers(LaunchContext context)
    {
        LoadoutId? target = context.LoadoutId == default ? null : context.LoadoutId;
        var game = context.Installation.Game.DisplayName;
        var installation = registry.TryGetMetadata(context.Installation, out var metadata) ? metadata.GameInstallMetadataId : default;

        foreach (var (root, backend) in MountingRoots(context.Installation))
        {
            var described = DeploymentRootNames.Describe(root);
            if (ServedHere(context.Installation, root))
            {
                logger.LogDebug("{Root} of {Game} is mounted by a run in this process; it is not recovered", described, game);
                continue;
            }

            var report = await backend.Recover(context.Installation, root, target, CancellationToken.None);
            if (installation != default) findings.Record(installation, root, report);

            switch (report.Status)
            {
                case MountRecoveryStatus.NeedsTarget:
                    logger.LogWarning("{Game}'s files staged under {Root} were left in {Staging}: no loadout is known to take them", game, described, report.Staging);
                    break;
                case MountRecoveryStatus.Blocked:
                    logger.LogWarning("{Game}'s files staged under {Root} were set aside in {Recovery} because they changed while being promoted", game, described, report.Recovery);
                    break;
                case MountRecoveryStatus.OverCap:
                    logger.LogWarning("{Game}'s files staged under {Root} were left in {Staging}: they are more than the promotion size cap", game, described, report.Staging);
                    break;
                case MountRecoveryStatus.Nothing:
                    break;
                default:
                    logger.LogInformation("Recovered {Root} of {Game}: {Status}, {Files} files promoted", described, game, report.Status, report.PromotedFiles);
                    break;
            }
        }
    }

    // The roots of the loadout's recorded plan that a mounting backend serves, in plan order.
    private IEnumerable<(GamePath Root, IMountingDeploymentBackend Backend)> MountingRoots(LaunchContext context) => MountingRoots(context.Installation);

    private IEnumerable<(GamePath Root, IMountingDeploymentBackend Backend)> MountingRoots(GameInstallation installation)
    {
        if (!registry.TryGetMetadata(installation, out var metadata)) yield break;

        foreach (var row in plans.Load(connection.Db, metadata.GameInstallMetadataId).Roots)
        {
            if (_mounting.TryGetValue(row.Backend, out var backend)) yield return (row.Root, backend);
        }
    }
}
