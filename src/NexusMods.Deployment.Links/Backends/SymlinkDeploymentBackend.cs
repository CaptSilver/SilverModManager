using System.Globalization;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Deployment.Links.Native;
using NexusMods.Deployment.Links.Probe;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Native;

namespace NexusMods.Deployment.Links.Backends;

/// <summary>
/// Deploys each file as a symlink holding the absolute path of its staged copy. Works across filesystems,
/// but the game process has to be able to see the staging store.
/// </summary>
public sealed class SymlinkDeploymentBackend(CopyDeploymentBackend copy, ILooseStagingStore store, IGameRegistry gameRegistry, ILogger<SymlinkDeploymentBackend> logger)
    : LinkDeploymentBackendBase(copy, store, gameRegistry, logger)
{
    /// <inheritdoc />
    public override DeploymentBackendId Id => DeploymentBackendId.Symlink;

    /// <inheritdoc />
    public override DeploymentPreflight Preflight(GameInstallation installation, GamePath root)
    {
        return LinkProbe.TrySymlink(ProbeDirectory(installation), installation.Locations.ToAbsolutePath(root), out var reason)
            ? DeploymentPreflight.Accept(AcceptedReason(Store.Root, Store.Root.FileSystem.GetKnownPath(KnownPath.HomeDirectory)))
            : DeploymentPreflight.Refuse(reason);
    }

    // The Steam runtime container shares the home directory by default, so a store under it is visible to the game
    // as it is; anywhere else the link dangles inside the container unless the run line adds the store to the mounts.
    internal static string AcceptedReason(AbsolutePath storeRoot, AbsolutePath home)
    {
        const string works = "symlinks work in the folder";
        return storeRoot.InFolder(home) || storeRoot == home
            ? $"{works}; the store is under the home directory, which the Steam runtime container shares"
            : $"{works}; store root {storeRoot} is outside the home directory: the game sees symlink targets only when launched through the run line";
    }

    /// <inheritdoc />
    /// <remarks>
    /// Inside the Steam runtime container a symlink into the staging store dangles unless the store is shared with it.
    /// </remarks>
    public override IReadOnlyList<AbsolutePath> RequiredFilesystems(GameInstallation installation, GamePath root) => [Store.Root];

    /// <inheritdoc />
    protected override bool SharesStoreFile => true;

    /// <inheritdoc />
    protected override int Place(AbsolutePath storeFile, AbsolutePath target) => LibcLinks.TrySymlink(storeFile.ToString(), target.ToString());

    /// <inheritdoc />
    protected override bool HasLinkIdentity(AbsolutePath deployed, AbsolutePath storeFile)
    {
        return LibcStat.TryLStat(deployed.ToString(), out var stat) == 0
               && (stat.Mode & LibcStat.TypeMask) == LibcStat.Symlink
               && LibcLinks.TryReadLink(deployed.ToString(), out var target) == 0
               && target == storeFile.ToString();
    }

    /// <inheritdoc />
    protected override IReadOnlySet<Hash> HashesLinkedElsewhere(DeploymentContext context, IReadOnlyDictionary<Hash, AbsolutePath> storeFiles)
    {
        // Disk state is the only record of what earlier applies linked; a symlink holds no count. Only the
        // paths whose content is one of the hashes being extracted can be a link to one of these files.
        var linked = new HashSet<Hash>();
        foreach (var (path, node) in context.SyncTree)
        {
            if (!node.HaveDisk || !storeFiles.TryGetValue(node.Disk.Hash, out var storeFile) || linked.Contains(node.Disk.Hash)) continue;
            if (HasLinkIdentity(context.Locations.ToAbsolutePath(path), storeFile)) linked.Add(node.Disk.Hash);
        }
        return linked;
    }

    /// <inheritdoc />
    protected override bool TryFindLinkedStoreHash(GameInstallMetadataId installation, AbsolutePath deployed, Func<IReadOnlyDictionary<(ulong Device, ulong Inode), Hash>> storeInodes, out Hash hash)
    {
        hash = default;
        if (LibcStat.TryLStat(deployed.ToString(), out var stat) != 0 || (stat.Mode & LibcStat.TypeMask) != LibcStat.Symlink) return false;
        if (LibcLinks.TryReadLink(deployed.ToString(), out var target) != 0) return false;

        // Staged files are named by the 16 hex digits of their hash; anything else is not a link into the store.
        var name = Path.GetFileName(target);
        if (name.Length != 16 || !ulong.TryParse(name, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)) return false;

        hash = Hash.From(value);
        return Store.PathFor(installation, hash).ToString() == target;
    }

    /// <inheritdoc />
    protected override void RepairStoreAfterWriteThrough(GameInstallMetadataId installation, Hash previous, AbsolutePath deployed, AbsolutePath storeFile)
    {
        var modified = File.GetLastWriteTimeUtc(storeFile.ToString());

        // The link goes first. Moving the file over the live link would copy it when the store is on another
        // device, and that copy opens its destination with O_TRUNC, follows the link, and empties the very
        // file it is reading.
        File.Delete(deployed.ToString());
        File.Move(storeFile.ToString(), deployed.ToString(), overwrite: false);

        // A cross-device move is a copy; keep the time the index recorded for the edited file.
        File.SetLastWriteTimeUtc(deployed.ToString(), modified);
    }
}
