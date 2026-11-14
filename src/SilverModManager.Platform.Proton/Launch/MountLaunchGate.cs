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
    IDeploymentBackendSelector selector,
    ILogger<MountLaunchGate> logger) : ILaunchGate
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
        if (!_scopes.TryRemove(context.SessionId, out var scope)) return;

        await using (scope)
        {
            var busy = await scope.FinishNamingBusyRoots(CancellationToken.None);
            if (busy.Count == 0) return;

            var game = context.Installation.Game.DisplayName;
            logger.LogWarning("{Game} left files open under {Roots} after it exited", game, busy);
            throw MountStillInUseException.For(game, busy);
        }
    }

    // The roots of the loadout's recorded plan that a mounting backend serves, in plan order.
    private IEnumerable<(GamePath Root, IMountingDeploymentBackend Backend)> MountingRoots(LaunchContext context)
    {
        var installation = Loadout.Load(connection.Db, context.LoadoutId).InstallationId;
        foreach (var row in plans.Load(connection.Db, installation).Roots)
        {
            if (_mounting.TryGetValue(row.Backend, out var backend)) yield return (row.Root, backend);
        }
    }
}
