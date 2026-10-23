namespace NexusMods.Deployment.Links.Probe;

/// <summary>
/// Symbolic names for the errnos a link attempt can return, so a refusal reads
/// <c>EXDEV (cross-device)</c> instead of <c>errno 18</c>.
/// </summary>
internal static class ErrnoText
{
    /// <summary>
    /// <paramref name="specificErrno"/> and <paramref name="specificMeaning"/> replace the generic
    /// meaning when the same errno means something narrower for one call (EOPNOTSUPP from FICLONE).
    /// </summary>
    internal static string Describe(int errno, int specificErrno = 0, string? specificMeaning = null)
    {
        if (errno == specificErrno && specificMeaning is not null)
            return $"{Name(errno)} ({specificMeaning})";

        return errno switch
        {
            1 => "EPERM (operation not permitted)",
            2 => "ENOENT (no such file or directory)",
            13 => "EACCES (permission denied)",
            17 => "EEXIST (already exists)",
            18 => "EXDEV (cross-device)",
            22 => "EINVAL (invalid argument)",
            28 => "ENOSPC (no space left)",
            30 => "EROFS (read-only filesystem)",
            31 => "EMLINK (too many links)",
            95 => "EOPNOTSUPP (operation not supported)",
            _ => $"errno {errno}",
        };
    }

    private static string Name(int errno) => Describe(errno).Split(' ')[0];
}
