using NexusMods.Paths;

namespace SilverModManager.Deployment.Staging;

/// <summary>
/// Tells filesystems apart and finds where one starts, so a store can sit on the filesystem a game folder is on.
/// </summary>
public interface IFilesystemLocator
{
    /// <summary>
    /// The <c>st_dev</c> of the nearest existing ancestor of <paramref name="path"/>, or null when it cannot be read.
    /// </summary>
    ulong? DeviceOf(AbsolutePath path);

    /// <summary>
    /// The topmost directory with the same device as <paramref name="path"/>: the mount point, or the top of a
    /// btrfs subvolume. Null when the device cannot be read.
    /// </summary>
    AbsolutePath? RootOf(AbsolutePath path);
}
