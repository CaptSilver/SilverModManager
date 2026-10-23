using NexusMods.Paths;

namespace NexusMods.Deployment.Vfs;

/// <summary>
/// A live mount returned by <see cref="IFuseKernel.Mount"/>. Disposing unmounts.
/// </summary>
public interface IFuseMount : IAsyncDisposable
{
    AbsolutePath MountPoint { get; }

    /// <summary>
    /// Completes when the session loop has exited.
    /// </summary>
    Task Completion { get; }

    /// <summary>
    /// Drops the kernel's cached dentry for <paramref name="name"/> under <paramref name="parent"/>.
    /// </summary>
    void InvalidateEntry(ulong parent, string name);

    /// <summary>
    /// Drops the kernel's cached attributes and data for <paramref name="ino"/>.
    /// </summary>
    void InvalidateInode(ulong ino);

    Task Unmount(CancellationToken token);
}
