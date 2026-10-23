using NexusMods.Paths;

namespace NexusMods.Deployment.Fuse.Mount;

public enum UnmountStatus
{
    Unmounted,

    /// <summary>Nothing was mounted there, which is what a caller cleaning up wants anyway.</summary>
    NotMounted,

    Failed,
}

public readonly record struct UnmountResult(UnmountStatus Status, int ExitCode, string StdErr);

public interface IFuseUnmounter
{
    /// <summary>
    /// Unmounts with <c>fusermount3</c>. A lazy unmount detaches the mount even while files are open, which is the
    /// fallback for a busy mount whose session has already died.
    /// </summary>
    /// <remarks>
    /// <paramref name="token"/> is only checked before fusermount3 starts; once it runs, it runs to its own deadline.
    /// A plain unmount that overruns that deadline is retried as a lazy one and reports <see cref="UnmountStatus.Unmounted"/>
    /// if that succeeds. When the lazy run overruns too, the result is <see cref="UnmountStatus.Failed"/> with exit
    /// code -1.
    /// </remarks>
    Task<UnmountResult> Unmount(AbsolutePath mountPoint, bool lazy, CancellationToken token);
}
