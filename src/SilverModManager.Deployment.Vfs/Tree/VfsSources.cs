using SilverModManager.Abstractions.Loadouts.Synchronizers;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Deployment.Vfs;

/// <summary>
/// What a mounted folder serves on top of its backing directory. <paramref name="Unserved"/> lists
/// intrinsic files under the folder that nothing can serve yet; the caller has to deal with them.
/// </summary>
public sealed record VfsSourceSet(IReadOnlyList<VfsFileSource> Sources, IReadOnlyList<GamePath> Unserved);

/// <summary>
/// Turns the synchronizer's answer into tree sources. The sync tree is the truth about who won: nothing
/// here looks at groups or priorities, and nothing infers deletes from what is on disk.
/// </summary>
public static class VfsSources
{
    /// <param name="tree">The synchronizer's tree. It never holds a winning delete.</param>
    /// <param name="deletedWinners">The paths whose winner is a delete, from the same winning-files query as the tree.</param>
    /// <param name="mountRoot">The mounted folder. Source paths come out relative to it.</param>
    /// <param name="stagedPathFor">Where a loadout file's bytes are in the staging store. Nothing is extracted here.</param>
    public static VfsSourceSet FromSyncTree(
        IReadOnlyDictionary<GamePath, SyncNode> tree,
        IReadOnlyCollection<GamePath> deletedWinners,
        GamePath mountRoot,
        Func<Hash, AbsolutePath> stagedPathFor)
    {
        var sources = new List<VfsFileSource>();
        var unserved = new List<GamePath>();

        foreach (var (path, node) in tree)
        {
            if (!IsUnder(path, mountRoot)) continue;

            switch (node.SourceItemType)
            {
                case LoadoutSourceItemType.Loadout when node.HaveLoadout:
                    sources.Add(new VfsFileSource(
                        RelativeTo(path, mountRoot),
                        VfsSourceKind.Staged,
                        stagedPathFor(node.Loadout.Hash),
                        node.Loadout.Size,
                        node.Loadout.Hash));
                    break;

                // A vanilla file, or a file only the disk knows about (MergeStates gives those the default
                // item type and no loadout hash): whatever the backing directory holds is what gets served.
                case LoadoutSourceItemType.Game:
                case LoadoutSourceItemType.Loadout:
                    break;

                case LoadoutSourceItemType.Intrinsic:
                    unserved.Add(path);
                    break;

                default:
                    throw new InvalidOperationException($"A sync tree node at `{path}` has item type `{node.SourceItemType}`, which the tree never holds.");
            }
        }

        foreach (var path in deletedWinners)
        {
            if (!IsUnder(path, mountRoot)) continue;
            sources.Add(new VfsFileSource(RelativeTo(path, mountRoot), VfsSourceKind.Hidden, RealPath: null, Size.Zero, Hash.Zero));
        }

        return new VfsSourceSet(sources, unserved);
    }

    // Whole segments and case-insensitive, the rule the deployment plan uses to route a path to a root.
    private static bool IsUnder(GamePath path, GamePath root) =>
        path.LocationId == root.LocationId
        && (root.Path == RelativePath.Empty || path.Path == root.Path || path.Path.InFolder(root.Path));

    private static RelativePath RelativeTo(GamePath path, GamePath root)
    {
        if (root.Path == RelativePath.Empty) return path.Path;
        if (path.Path == root.Path) throw new InvalidOperationException($"`{path}` is the mounted folder itself, not a file in it.");
        return path.Path.RelativeTo(root.Path);
    }
}
