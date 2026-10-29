namespace SilverModManager.Sdk.Hashes;

/// <summary>
/// Limits shared by every loop that streams whole game files through a hasher.
/// </summary>
public static class HashingLimits
{
    /// <summary>
    /// How many files are read at once. A loop with no cap starts one read per CPU thread, which on a large
    /// modded game saturates the disk and starves the rest of the app.
    /// </summary>
    public const int FileReaders = 4;

    /// <summary>
    /// How many bytes one read asks for when a whole file is streamed through a hasher. On an NVMe disk a read of
    /// 8 KB moves about 107 MB/s, 128 KB about 1.4 GB/s and 1 MB about 2.6 GB/s (uncached, btrfs on LUKS), so a small
    /// block spends the disk's time on request overhead instead of data.
    /// </summary>
    public const int ReadBlockSize = 1024 * 1024;
}
