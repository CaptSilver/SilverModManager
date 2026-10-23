using NexusMods.Paths;

namespace NexusMods.Deployment.Vfs;

/// <summary>
/// What <c>stat</c> says about a file, used to decide whether a remembered hash still describes it.
/// Every field has to match: a file can be rewritten with the same size and mtime, and then only
/// the ctime or inode gives it away.
/// </summary>
public readonly record struct FileFingerprint(ulong Device, ulong Inode, Size Size, DateTimeOffset MTime, DateTimeOffset CTime)
{
    public static FileFingerprint From(BackingEntry entry) => new(entry.Device, entry.Inode, entry.Size, entry.MTime, entry.CTime);
}
