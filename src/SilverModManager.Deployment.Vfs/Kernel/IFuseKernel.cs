namespace SilverModManager.Deployment.Vfs;

/// <summary>
/// Mounts an <see cref="IVfsOperations"/> as a kernel filesystem. The real implementation binds
/// libfuse; <see cref="Testing.FakeFuseKernel"/> drives the same operations without a kernel.
/// </summary>
public interface IFuseKernel
{
    /// <summary>
    /// Returns once the filesystem is mounted and serving; the session runs until
    /// <see cref="IFuseMount.Unmount"/> or token cancellation.
    /// </summary>
    Task<IFuseMount> Mount(IVfsOperations operations, FuseMountOptions options, CancellationToken token);
}
