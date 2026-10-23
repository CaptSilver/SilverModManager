using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Deployment.Links.Native;
using SilverModManager.Deployment.Links.Probe;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Deployment.Links.Backends;

/// <summary>
/// Deploys each file as a copy-on-write clone of its staged copy. Every clone is its own inode, so an edit
/// touches only that path and nothing needs repairing.
/// </summary>
public sealed class ReflinkDeploymentBackend(CopyDeploymentBackend copy, ILooseStagingStore store, IGameRegistry gameRegistry, ILogger<ReflinkDeploymentBackend> logger)
    : LinkDeploymentBackendBase(copy, store, gameRegistry, logger)
{
    /// <inheritdoc />
    public override DeploymentBackendId Id => DeploymentBackendId.Reflink;

    /// <inheritdoc />
    public override DeploymentPreflight Preflight(GameInstallation installation, GamePath root)
    {
        return LinkProbe.TryReflink(ProbeDirectory(installation, root), installation.Locations.ToAbsolutePath(root), out var reason)
            ? DeploymentPreflight.Accept("the filesystem clones files between the staging store and the folder")
            : DeploymentPreflight.Refuse(reason);
    }

    /// <inheritdoc />
    public override IReadOnlyList<AbsolutePath> RequiredFilesystems(GameInstallation installation, GamePath root) => [];

    /// <inheritdoc />
    protected override bool SharesStoreFile => false;

    /// <inheritdoc />
    protected override int Place(AbsolutePath storeFile, AbsolutePath target)
    {
        int errno;
        using (var source = File.OpenHandle(storeFile.ToString()))
        using (var destination = File.OpenHandle(target.ToString(), FileMode.CreateNew, FileAccess.Write))
        {
            errno = LibcLinks.TryClone(source, destination);
        }

        // A refused clone leaves the empty file CreateNew made.
        if (errno != 0) File.Delete(target.ToString());
        return errno;
    }

    /// <inheritdoc />
    protected override bool HasLinkIdentity(AbsolutePath deployed, AbsolutePath storeFile) => false;

    /// <inheritdoc />
    protected override IReadOnlySet<Hash> HashesLinkedElsewhere(DeploymentContext context, IReadOnlyDictionary<Hash, AbsolutePath> storeFiles) => new HashSet<Hash>();

    /// <inheritdoc />
    protected override bool TryFindLinkedStoreHash(GameInstallMetadataId installation, AbsolutePath deployed, Func<IReadOnlyDictionary<(ulong Device, ulong Inode), Hash>> storeInodes, out Hash hash)
    {
        hash = default;
        return false;
    }

    /// <inheritdoc />
    protected override void RepairStoreAfterWriteThrough(ILooseStagingStore store, GameInstallMetadataId installation, Hash previous, AbsolutePath deployed, AbsolutePath storeFile)
        => throw new InvalidOperationException("A reflink never shares an inode with the staged file, so there is nothing to repair.");
}
