using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Deployment.Links.Probe;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Hashes;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Deployment.Links.Backends;

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
        return LinkProbe.TryHardlink(ProbeDirectory(installation, root), installation.Locations.ToAbsolutePath(root), out var reason)
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
    protected override IReadOnlySet<FileIdentity> HashesLinkedElsewhere(DeploymentContext context, IReadOnlyDictionary<FileIdentity, AbsolutePath> storeFiles)
    {
        // A staged file nothing links to has one name: its own. Another one means some game path holds it.
        var linked = new HashSet<FileIdentity>();
        foreach (var (identity, storeFile) in storeFiles)
        {
            if (LibcStat.TryStat(storeFile.ToString(), out var stat) == 0 && stat.NLink >= 2) linked.Add(identity);
        }
        return linked;
    }

    /// <inheritdoc />
    protected override bool TryFindLinkedStoreHash(GameInstallMetadataId installation, AbsolutePath deployed, Func<IReadOnlyDictionary<(ulong Device, ulong Inode), FileIdentity>> storeInodes, out FileIdentity identity)
    {
        // A staged file that nothing is linked to has one name; a deployed file with one name cannot be a link to it.
        identity = default;
        return LibcStat.TryStat(deployed.ToString(), out var stat) == 0
               && stat.NLink >= 2
               && storeInodes().TryGetValue((stat.Device, stat.Inode), out identity);
    }

    /// <inheritdoc />
    protected override void RepairStoreAfterWriteThrough(ILooseStagingStore store, GameInstallMetadataId installation, FileIdentity previous, AbsolutePath deployed, AbsolutePath storeFile)
    {
        // The game path keeps the inode and the bytes written to it. The staged name goes, and Ensure
        // extracts the file from the archives the next time it is needed.
        store.Release(installation, previous);
    }
}
