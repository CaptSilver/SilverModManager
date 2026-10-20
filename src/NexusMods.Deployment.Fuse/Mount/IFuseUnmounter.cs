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
    Task<UnmountResult> Unmount(AbsolutePath mountPoint, bool lazy, CancellationToken token);
}
