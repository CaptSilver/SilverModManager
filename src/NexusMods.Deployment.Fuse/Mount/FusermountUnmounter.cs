using NexusMods.Paths;

namespace NexusMods.Deployment.Fuse.Mount;

internal sealed class FusermountUnmounter(IFusermountRunner runner) : IFuseUnmounter
{
    /// <summary>
    /// How long one fusermount3 run may take. A plain unmount has been seen to run past 30 s with every core busy,
    /// far longer than a caller's short timeout.
    /// </summary>
    internal TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(30);

    // The caller's token is not handed to the runner: cancelling it kills fusermount3 partway, which can leave the
    // mount behind for whoever mounts there next.
    public async Task<UnmountResult> Unmount(AbsolutePath mountPoint, bool lazy, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var result = await RunBounded(mountPoint, lazy).ConfigureAwait(false);
        if (result is null && !lazy) result = await RunBounded(mountPoint, lazy: true).ConfigureAwait(false);
        return result ?? new UnmountResult(UnmountStatus.Failed, -1, $"fusermount3 did not finish within {Deadline.TotalSeconds:0.###} s");
    }

    private async Task<UnmountResult?> RunBounded(AbsolutePath mountPoint, bool lazy)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        try
        {
            // "--" so a mount point that starts with a dash is not read as an option.
            var (exitCode, stdErr) = await runner.Run([lazy ? "-uz" : "-u", "--", mountPoint.ToString()], deadline.Token).ConfigureAwait(false);
            if (exitCode == 0) return new UnmountResult(UnmountStatus.Unmounted, exitCode, stdErr);

            // fusermount3 refuses a path with no mount table entry before unmounting anything; if it does reach
            // umount2 on a path that is not a mount point, the kernel answers EINVAL.
            var notMounted = stdErr.Contains("not found in /etc/mtab", StringComparison.Ordinal)
                             || stdErr.Contains("Invalid argument", StringComparison.Ordinal);
            return new UnmountResult(notMounted ? UnmountStatus.NotMounted : UnmountStatus.Failed, exitCode, stdErr);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return null;
        }
    }
}
