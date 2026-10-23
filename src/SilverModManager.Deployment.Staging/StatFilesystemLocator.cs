using NexusMods.Paths;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Deployment.Staging;

/// <summary>
/// Compares <c>st_dev</c> along the path instead of reading the mount table. Not <c>statfs</c>'s <c>f_fsid</c>:
/// btrfs subvolumes share a filesystem id but report different devices, and link(2) fails EXDEV across them.
/// </summary>
public sealed class StatFilesystemLocator : IFilesystemLocator
{
    /// <inheritdoc/>
    public ulong? DeviceOf(AbsolutePath path)
    {
        var existing = NearestExisting(path);
        return LibcStat.TryStat(existing.ToString(), out var stat) == 0 ? stat.Device : null;
    }

    /// <inheritdoc/>
    public AbsolutePath? RootOf(AbsolutePath path)
    {
        if (DeviceOf(path) is not { } device) return null;

        var top = NearestExisting(path);
        while (top.Parent != top && LibcStat.TryStat(top.Parent.ToString(), out var parent) == 0 && parent.Device == device)
            top = top.Parent;
        return top;
    }

    private static AbsolutePath NearestExisting(AbsolutePath path)
    {
        var current = path;
        while (!current.DirectoryExists() && current.Parent != current) current = current.Parent;
        return current;
    }
}
