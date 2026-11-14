using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <inheritdoc />
public sealed class DeploymentMountScope(
    IDeploymentPlanStore plans,
    IEnumerable<IDeploymentBackend> backends,
    IConnection connection,
    HeldMounts held,
    ILogger<DeploymentMountScope> logger) : IDeploymentMountScope
{
    private readonly Dictionary<DeploymentBackendId, IMountingDeploymentBackend> _mounting =
        backends.OfType<IMountingDeploymentBackend>().ToDictionary(static b => b.Id);

    // In mount order; Finish walks it backwards.
    private readonly List<(IDeploymentMount Mount, IDisposable Hold)> _up = [];
    private readonly List<GamePath> _roots = [];

    /// <inheritdoc />
    public IReadOnlyList<GamePath> MountedRoots => _roots.ToArray();

    /// <inheritdoc />
    public async Task Mount(GameInstallation installation, Loadout.ReadOnly loadout, CancellationToken token)
    {
        foreach (var row in plans.Load(connection.Db, loadout.InstallationId).Roots)
        {
            if (!_mounting.TryGetValue(row.Backend, out var backend)) continue;

            try
            {
                var mount = await backend.Mount(installation, loadout, row.Root, token);
                _up.Add((mount, held.Hold(installation, row.Root)));
                _roots.Add(row.Root);
            }
            catch
            {
                // A run that cannot get all of its mounts must not leave the first ones serving a folder nobody finishes.
                try
                {
                    await Finish(CancellationToken.None);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Finishing the mounts that were up when mounting {Root} failed", row.Root);
                }
                throw;
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MountFinishResult>> Finish(CancellationToken token)
    {
        var results = new List<MountFinishResult>(_up.Count);
        Exception? first = null;
        for (var i = _up.Count - 1; i >= 0; i--)
        {
            var (mount, hold) = _up[i];
            try
            {
                results.Add(await mount.Finish(token));
            }
            catch (Exception e)
            {
                logger.LogError(e, "Finishing the mount of {Root} failed", mount.Root);
                first ??= e;
            }
            finally
            {
                // A finish that threw still lets the mount go; the hold goes last so the window close waits for it.
                try
                {
                    await mount.DisposeAsync();
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Releasing the mount of {Root} failed", mount.Root);
                    first ??= e;
                }
                hold.Dispose();
            }
        }

        _up.Clear();
        _roots.Clear();
        if (first is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        return results;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_up.Count == 0) return;
        try
        {
            await Finish(CancellationToken.None);
        }
        catch (Exception e)
        {
            // Dispose runs on the way out of a run that may already be failing; the finish logged its own cause.
            logger.LogError(e, "Finishing the mounts left over at the end of a run failed");
        }
    }
}

/// <inheritdoc />
public sealed class DeploymentMountScopeFactory(IServiceProvider services) : IDeploymentMountScopeFactory
{
    public IDeploymentMountScope Create() => Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<DeploymentMountScope>(services);
}
