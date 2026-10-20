using NexusMods.Paths;

namespace NexusMods.Deployment.Fuse.Mount;

internal sealed class FusermountUnmounter(IFusermountRunner runner) : IFuseUnmounter
{
    public async Task<UnmountResult> Unmount(AbsolutePath mountPoint, bool lazy, CancellationToken token)
    {
        // "--" so a mount point that starts with a dash is not read as an option.
        var (exitCode, stdErr) = await runner.Run([lazy ? "-uz" : "-u", "--", mountPoint.ToString()], token);
        if (exitCode == 0) return new UnmountResult(UnmountStatus.Unmounted, exitCode, stdErr);

        // fusermount3 refuses a path with no mount table entry before unmounting anything; if it does reach
        // umount2 on a path that is not a mount point, the kernel answers EINVAL.
        var notMounted = stdErr.Contains("not found in /etc/mtab", StringComparison.Ordinal)
                         || stdErr.Contains("Invalid argument", StringComparison.Ordinal);
        return new UnmountResult(notMounted ? UnmountStatus.NotMounted : UnmountStatus.Failed, exitCode, stdErr);
    }
}
