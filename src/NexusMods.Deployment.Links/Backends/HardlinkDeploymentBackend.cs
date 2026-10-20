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
/// Deploys each file as a hard link to its staged copy. Works where the game folder and the staging store
/// are on one filesystem.
/// </summary>
public sealed class HardlinkDeploymentBackend(CopyDeploymentBackend copy, ILooseStagingStore store, IGameRegistry gameRegistry, ILogger<HardlinkDeploymentBackend> logger)
    : LinkDeploymentBackendBase(copy, store, gameRegistry, logger)
{
    /// <inheritdoc />
    public override DeploymentBackendId Id => DeploymentBackendId.Hardlink;

    /// <inheritdoc />
    public override DeploymentPreflight Preflight(GameInstallation installation, GamePath root)
    {
        return LinkProbe.TryHardlink(ProbeDirectory(installation), installation.Locations.ToAbsolutePath(root), out var reason)
            ? DeploymentPreflight.Accept("hard links work between the staging store and the folder")
            : DeploymentPreflight.Refuse(reason);
    }

    /// <inheritdoc />
    public override IReadOnlyList<AbsolutePath> RequiredFilesystems(GameInstallation installation, GamePath root) => [];

    /// <inheritdoc />
    protected override bool SharesStoreFile => true;

    /// <inheritdoc />
    protected override int Place(AbsolutePath storeFile, AbsolutePath target) => LibcLinks.TryLink(storeFile.ToString(), target.ToString());

    /// <inheritdoc />
    protected override bool HasLinkIdentity(AbsolutePath deployed, AbsolutePath storeFile)
    {
        return LibcStat.TryStat(storeFile.ToString(), out var staged) == 0
               && LibcStat.TryStat(deployed.ToString(), out var linked) == 0
               && staged.Device == linked.Device
               && staged.Inode == linked.Inode;
    }

    /// <inheritdoc />
    protected override IReadOnlySet<Hash> HashesLinkedElsewhere(DeploymentContext context, IReadOnlyDictionary<Hash, AbsolutePath> storeFiles)
    {
        // A staged file nothing links to has one name: its own. Another one means some game path holds it.
        var linked = new HashSet<Hash>();
        foreach (var (hash, storeFile) in storeFiles)
        {
            if (LibcStat.TryStat(storeFile.ToString(), out var stat) == 0 && stat.NLink >= 2) linked.Add(hash);
        }
        return linked;
    }

    /// <inheritdoc />
    protected override bool TryFindLinkedStoreHash(GameInstallMetadataId installation, AbsolutePath deployed, Func<IReadOnlyDictionary<(ulong Device, ulong Inode), Hash>> storeInodes, out Hash hash)
    {
        // A staged file that nothing is linked to has one name; a deployed file with one name cannot be a link to it.
        hash = default;
        return LibcStat.TryStat(deployed.ToString(), out var stat) == 0
               && stat.NLink >= 2
               && storeInodes().TryGetValue((stat.Device, stat.Inode), out hash);
    }

    /// <inheritdoc />
    protected override void RepairStoreAfterWriteThrough(GameInstallMetadataId installation, Hash previous, AbsolutePath deployed, AbsolutePath storeFile)
    {
        // The game path keeps the inode and the bytes written to it. The staged name goes, and Ensure
        // extracts the hash from the archives the next time it is needed.
        Store.Release(installation, previous);
    }
}
