using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Deployment.Fuse.Availability;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Deployment.Fuse.Backend;

/// <summary>
/// Whether a folder can be served through a mount, as the first thing that is wrong with it. Nothing here creates a
/// file: a mount is not something to probe while a game may be running.
/// </summary>
internal static class FusePreflight
{
    /// <param name="intrinsicPaths">The paths of every file the game generates for any loadout of the installation.</param>
    /// <param name="isMounted">Whether the kernel lists a mount of ours at a root. Only asked once the folder exists.</param>
    internal static DeploymentPreflight Evaluate(
        GameInstallation installation,
        GamePath root,
        DeploymentPlan plan,
        FuseAvailabilityReport fuse,
        Func<GamePath, bool> isMounted,
        IReadOnlyCollection<GamePath> intrinsicPaths)
    {
        var game = installation.Game.DisplayName;

        var mountable = installation.Game is IMountableGame declaring ? declaring.MountRoots(installation) : [];
        if (!mountable.Contains(root))
        {
            var list = mountable.Count == 0 ? "none" : string.Join(", ", mountable.Select(Describe));
            return DeploymentPreflight.Refuse($"{game} does not declare {Describe(root)} as a folder that can be mounted; mountable: {list}");
        }

        foreach (var other in plan.Roots)
        {
            if (other.Backend == DeploymentBackendId.Fuse && other.Root != root)
                return DeploymentPreflight.Refuse($"{game} already mounts {Describe(other.Root)}; one mounted folder per game");
        }

        if (!fuse.Usable) return DeploymentPreflight.Refuse(fuse.Reason ?? "FUSE is not usable here");

        var folder = installation.Locations.ToAbsolutePath(root);
        if (RealDirectory(folder) is null) return DeploymentPreflight.Refuse($"{folder} does not exist");

        if (installation.Game is ITimestampOrderedGame)
            return DeploymentPreflight.Refuse($"{game} orders plugins by file time, which a mounted folder cannot present yet");

        foreach (var intrinsic in intrinsicPaths)
        {
            if (intrinsic.InFolder(root))
                return DeploymentPreflight.Refuse($"{game} writes {Describe(intrinsic)} under {Describe(root)}, which a mounted folder cannot serve");
        }

        // Last: the lookup resolves the folder's parent, which has to exist.
        if (isMounted(root)) return DeploymentPreflight.Refuse($"{Describe(root)} is already mounted; run silvermodmanager doctor");

        return DeploymentPreflight.Accept("served through a mount while the game or a tool runs");
    }

    /// <summary>
    /// The folder as it is spelled on disk, or null when there is none. A game's data folder is often spelled
    /// <c>data</c> where the game's definition says <c>Data</c>, and a game path compares without regard to case.
    /// </summary>
    internal static AbsolutePath? RealDirectory(AbsolutePath folder)
    {
        if (folder.DirectoryExists()) return folder;
        if (folder.Parent == folder) return null;

        var parent = RealDirectory(folder.Parent);
        if (parent is not { } found) return null;

        var name = folder.FileName;
        foreach (var candidate in Directory.EnumerateDirectories(found.ToString()))
        {
            var candidateName = Path.GetFileName(candidate);
            if (string.Equals(candidateName, name, StringComparison.OrdinalIgnoreCase)) return found.Combine(candidateName);
        }
        return null;
    }

    private static string Describe(GamePath root) => DeploymentRootNames.Describe(root);
}
