using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>A root a launch serves through a mount, and where the folder it covers is.</summary>
public readonly record struct MountedRoot(GamePath Root, DeploymentBackendId Backend, AbsolutePath Folder);

/// <summary>
/// What the recorded deployment plan means for one launch.
/// </summary>
/// <param name="FilesystemRoots">Host folders the game's process must be able to see, which a Steam wrapper reads from the state file.</param>
/// <param name="Mounted">The roots served through a mount while the game runs.</param>
public sealed record LaunchMounts(IReadOnlyList<string> FilesystemRoots, IReadOnlyList<MountedRoot> Mounted)
{
    public static LaunchMounts None { get; } = new([], []);
}

/// <summary>
/// Reads the recorded deployment plan for a launch.
/// </summary>
public interface ILaunchMounts
{
    LaunchMounts For(LaunchContext context);
}

/// <inheritdoc />
public sealed class DeploymentLaunchMounts(
    DeploymentLaunchRoots roots,
    IDeploymentPlanStore plans,
    IEnumerable<IDeploymentBackend> backends,
    IConnection connection) : ILaunchMounts
{
    private readonly HashSet<DeploymentBackendId> _mounting = backends.OfType<IMountingDeploymentBackend>().Select(static b => b.Id).ToHashSet();

    /// <inheritdoc />
    public LaunchMounts For(LaunchContext context)
    {
        var installation = Loadout.Load(connection.Db, context.LoadoutId).InstallationId;
        var mounted = plans.Load(connection.Db, installation).Roots
            .Where(row => _mounting.Contains(row.Backend))
            .Select(row => new MountedRoot(row.Root, row.Backend, context.Installation.Locations.ToAbsolutePath(row.Root)))
            .ToArray();
        return new LaunchMounts(roots.For(context.Installation, installation), mounted);
    }
}
