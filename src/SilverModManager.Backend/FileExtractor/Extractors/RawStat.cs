using System.Runtime.InteropServices;

namespace SilverModManager.Backend.FileExtractor.Extractors;

/// <summary>
/// lstat on a path given as bytes, so a name that is not valid UTF-8 can be asked about, and a link is described
/// rather than followed. The one stat binding the extractor code uses.
/// </summary>
internal static partial class RawStat
{
    internal enum Kind
    {
        Link,
        Folder,
        Other,
    }

    /// <param name="Kind">What the entry itself is, never what a link points at.</param>
    /// <param name="Mode">The permission bits of the entry itself.</param>
    /// <param name="Device">The device the entry is on, major and minor numbers joined.</param>
    /// <param name="Inode">The entry's inode on that device.</param>
    internal readonly record struct Info(Kind Kind, UnixFileMode Mode, ulong Device, ulong Inode);

    // statx has the same struct layout on every Linux architecture, unlike stat.
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static partial int StatX(int dirFd, byte[] path, int flags, uint mask, byte[] buffer);

    private const int CurrentDirectory = -100; // AT_FDCWD
    private const int NoFollow = 0x100; // AT_SYMLINK_NOFOLLOW
    private const uint BasicStats = 0x7ff; // STATX_BASIC_STATS
    private const int BufferSize = 256; // struct statx is 256 bytes
    private const int ModeOffset = 28; // stx_mode
    private const int InodeOffset = 32; // stx_ino
    private const int DeviceMajorOffset = 136; // stx_dev_major, then stx_dev_minor
    private const int FileTypeMask = 0xF000;
    private const int LinkType = 0xA000;
    private const int FolderType = 0x4000;
    private const int PermissionMask = 0xFFF;

    /// <summary>The entry at <paramref name="path"/> (NUL-terminated bytes) without following it, or null when it cannot be read; the errno is then <see cref="Marshal.GetLastPInvokeError"/>.</summary>
    internal static Info? Lstat(byte[] path)
    {
        var buffer = new byte[BufferSize];
        if (StatX(CurrentDirectory, path, NoFollow, BasicStats, buffer) != 0) return null;

        var bits = BitConverter.ToUInt16(buffer, ModeOffset);
        var kind = (bits & FileTypeMask) switch
        {
            LinkType => Kind.Link,
            FolderType => Kind.Folder,
            _ => Kind.Other,
        };
        var device = ((ulong)BitConverter.ToUInt32(buffer, DeviceMajorOffset) << 32) | BitConverter.ToUInt32(buffer, DeviceMajorOffset + 4);
        return new Info(kind, (UnixFileMode)(bits & PermissionMask), device, BitConverter.ToUInt64(buffer, InodeOffset));
    }
}
