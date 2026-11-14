using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// Refuses the writers while a mount from an earlier run is still over a root of the game.
/// </summary>
/// <remarks>
/// Under a mount the folder shows the loadout's files, so an apply that deletes "what is on disk" deletes through the
/// mount, and a re-index reads served mod files as changes somebody made. Recovery is what takes a mount away, and it
/// runs under <see cref="Suspend"/>.
/// </remarks>
public static class StaleMountGuard
{
    private static readonly AsyncLocal<bool> Suspended = new();

    /// <summary>
    /// Lets the current flow of execution through, until the returned scope is disposed: the work that removes a mount
    /// has to read and write the game's state while it is still there.
    /// </summary>
    public static IDisposable Suspend()
    {
        var previous = Suspended.Value;
        Suspended.Value = true;
        return new Scope(previous);
    }

    /// <exception cref="ExecutableInUseException">A root of the plan on a mounting backend is mounted now.</exception>
    public static void ThrowIfMounted(IReadOnlyDictionary<DeploymentBackendId, IDeploymentBackend> backends, DeploymentPlan plan, GameInstallation installation)
    {
        if (Suspended.Value) return;

        foreach (var root in plan.Roots)
        {
            if (!backends.TryGetValue(root.Backend, out var backend) || backend is not IMountingDeploymentBackend mounting) continue;
            if (!mounting.IsMounted(installation, root.Root)) continue;

            throw new ExecutableInUseException($"{DeploymentRootNames.Describe(root.Root)} is still mounted from an earlier run; run silvermodmanager doctor");
        }
    }

    /// <summary>
    /// The same check for a caller that holds only the service provider.
    /// </summary>
    public static void ThrowIfMounted(IServiceProvider serviceProvider, GameInstallation installation)
    {
        if (Suspended.Value) return;

        var backends = serviceProvider.GetServices<IDeploymentBackend>().ToDictionary(b => b.Id);
        if (!backends.Values.Any(b => b is IMountingDeploymentBackend)) return;

        var registry = serviceProvider.GetRequiredService<IGameRegistry>();
        if (!registry.TryGetMetadata(installation, out var metadata)) return;

        var plan = serviceProvider.GetRequiredService<IDeploymentPlanStore>().Load(serviceProvider.GetRequiredService<IConnection>().Db, metadata.GameInstallMetadataId);
        ThrowIfMounted(backends, plan, installation);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose() => Suspended.Value = previous;
    }
}
