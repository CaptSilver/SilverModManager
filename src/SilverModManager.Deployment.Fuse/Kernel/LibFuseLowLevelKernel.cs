using Microsoft.Extensions.Logging;
using SilverModManager.Deployment.Fuse.Availability;
using SilverModManager.Deployment.Fuse.Mount;
using SilverModManager.Deployment.Fuse.Native;
using SilverModManager.Deployment.Vfs;

namespace SilverModManager.Deployment.Fuse.Kernel;

/// <summary>
/// Mounts an <see cref="IVfsOperations"/> through libfuse3's low-level API. The library is bound by hand
/// (see <see cref="LibFuse3LowLevel"/>) because the high-level wrappers would walk paths and lose the inode
/// numbers the tree already owns.
/// </summary>
public sealed class LibFuseLowLevelKernel(ILogger<LibFuseLowLevelKernel> logger, IFuseUnmounter unmounter) : IFuseKernel
{
    private readonly IPassthroughRegistrar? _passthroughRegistrar;
    private readonly Func<string>? _readProcessStatus;

    // Lets a test script the kernel's answer to passthrough and the privileges the process appears to have.
    internal LibFuseLowLevelKernel(ILogger<LibFuseLowLevelKernel> logger, IFuseUnmounter unmounter, IPassthroughRegistrar registrar, string processStatus)
        : this(logger, unmounter)
    {
        _passthroughRegistrar = registrar;
        _readProcessStatus = () => processStatus;
    }

    public async Task<IFuseMount> Mount(IVfsOperations operations, FuseMountOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var (loaded, soname, version, error) = new FuseEnvironmentProbe().LoadLibrary();
        if (!loaded || soname is null || version is not { } fuseVersion)
            throw new InvalidOperationException($"libfuse3 could not be loaded: {error ?? "no soname or version reported"}");

        var abi = FuseAbi.Check(soname, fuseVersion);
        if (!abi.Ok) throw new InvalidOperationException($"unknown libfuse3 ABI: {abi.Reason}");
        if (abi.NewerThanVerified)
            logger.LogWarning("{Soname} fuse_version {Version} is newer than the verified {Verified}", soname, fuseVersion, FuseAbi.VerifiedVersion);

        var mountPoint = options.MountPoint.ToString();
        if (!Directory.Exists(mountPoint)) throw new DirectoryNotFoundException($"The mount point `{mountPoint}` is not a directory.");

        var mount = new LibFuseLowLevelMount(operations, options, logger, unmounter, soname, fuseVersion, LibcProcess.RealPath(mountPoint), _passthroughRegistrar, _readProcessStatus);
        await mount.Start().ConfigureAwait(false);

        // Cancelling the token ends the mount the same way disposing it does.
        token.Register(() => _ = mount.DisposeAsync().AsTask().ContinueWith(
            t => logger.LogError(t.Exception, "Unmounting `{MountPoint}` after cancellation failed", mountPoint),
            TaskContinuationOptions.OnlyOnFaulted));
        return mount;
    }
}
