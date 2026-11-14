using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Deployment.Vfs;

/// <summary>
/// Turns what a mount's manifest recorded into the sources the tree is built from. The real folder's own files stay
/// the tree's lower layer, so only what differs from it is listed here.
/// </summary>
public static class MountSourceBuilder
{
    /// <param name="manifest">The rows recorded for the mounted root.</param>
    /// <param name="deletedWinners">The paths whose winning loadout item is a delete; those under the root are hidden.</param>
    /// <param name="stagedPathFor">Where a loadout file's bytes are in the staging store. Nothing is extracted here.</param>
    /// <returns>Sources with paths relative to <see cref="MountManifestView.Root"/>.</returns>
    public static IReadOnlyList<VfsFileSource> Build(MountManifestView manifest, IReadOnlyCollection<GamePath> deletedWinners, Func<FileIdentity, AbsolutePath> stagedPathFor)
    {
        var sources = new List<VfsFileSource>(manifest.Rows.Count + deletedWinners.Count);
        foreach (var row in manifest.Rows)
        {
            var relative = RelativeTo(row.Path, manifest.Root);
            if (row.Hidden) sources.Add(Hidden(relative));
            else if (row.ServesTheRealFile) sources.Add(new VfsFileSource(relative, VfsSourceKind.Retimed, RealPath: null, row.Size, row.Identity, MTime: row.MTime));
            else sources.Add(new VfsFileSource(relative, VfsSourceKind.Staged, stagedPathFor(row.Identity), row.Size, row.Identity, MTime: row.MTime));
        }

        foreach (var path in deletedWinners)
        {
            if (!IsUnder(path, manifest.Root)) continue;
            sources.Add(Hidden(RelativeTo(path, manifest.Root)));
        }
        return sources;
    }

    private static VfsFileSource Hidden(RelativePath path) => new(path, VfsSourceKind.Hidden, RealPath: null, Size.Zero, FileIdentity.None);

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
