using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// The mounts one run of a game or tool holds: every mounting root of the installation's recorded plan, mounted after
/// the apply and finished, in the opposite order, before the ingest. A run takes one scope and drops it when it ends.
/// </summary>
public interface IDeploymentMountScope : IAsyncDisposable
{
    /// <summary>
    /// Mounts every mounting root of the loadout's installation. If one fails, the mounts already up are finished
    /// before the failure is rethrown. A plan with no mounting root mounts nothing.
    /// </summary>
    Task Mount(GameInstallation installation, Loadout.ReadOnly loadout, CancellationToken token);

    /// <summary>
    /// Finishes every mount this scope holds, last mounted first, and returns what each one did. Finishing is not
    /// abandoned when one mount throws: the rest are finished and the first failure is rethrown afterwards.
    /// </summary>
    Task<IReadOnlyList<MountFinishResult>> Finish(CancellationToken token);

    /// <summary>
    /// The roots this scope has mounted and not yet finished.
    /// </summary>
    IReadOnlyList<GamePath> MountedRoots { get; }
}

/// <summary>
/// Creates the scope for one run.
/// </summary>
public interface IDeploymentMountScopeFactory
{
    IDeploymentMountScope Create();
}

/// <summary>
/// What a run does with its scope's finish.
/// </summary>
public static class DeploymentMountScopeExtensions
{
    /// <summary>
    /// Finishes <paramref name="scope"/> and names the roots that came back <see cref="MountFinishStatus.StillInUse"/>,
    /// as <see cref="DeploymentRootNames.Describe"/> does, in the order they were finished.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FinishNamingBusyRoots(this IDeploymentMountScope scope, CancellationToken token)
    {
        // Finish reports in the opposite order to the mounts, and forgets the roots as it goes.
        var mounted = scope.MountedRoots.Reverse().ToList();
        var results = await scope.Finish(token);
        return mounted.Zip(results)
            .Where(static p => p.Second.Status == MountFinishStatus.StillInUse)
            .Select(static p => DeploymentRootNames.Describe(p.First))
            .ToList();
    }
}
