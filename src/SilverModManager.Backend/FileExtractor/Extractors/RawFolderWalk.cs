using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SilverModManager.Backend.FileExtractor.Extractors;

/// <summary>
/// Walks a folder tree by name bytes and reads or changes modes without following links. .NET lists a name that
/// is not valid UTF-8 with replacement characters, so the path it returns names nothing, and its mode calls
/// follow links; libc takes the bytes as they are on disk.
/// </summary>
internal static partial class RawFolderWalk
{
    internal enum Kind
    {
        Link,
        Folder,
        Other,
    }

    /// <param name="Path">NUL-terminated bytes of the entry's path.</param>
    /// <param name="Kind">What lstat says the entry itself is, never what a link points at.</param>
    /// <param name="Mode">The permission bits of the entry itself.</param>
    internal readonly record struct Entry(byte[] Path, Kind Kind, UnixFileMode Mode);

    [LibraryImport("libc", EntryPoint = "opendir", SetLastError = true)]
    private static partial nint OpenDir(byte[] path);

    [LibraryImport("libc", EntryPoint = "readdir")]
    private static partial nint ReadDir(nint dir);

    [LibraryImport("libc", EntryPoint = "closedir")]
    private static partial int CloseDir(nint dir);

    // statx has the same struct layout on every Linux architecture, unlike stat.
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static partial int StatX(int dirFd, byte[] path, int flags, uint mask, byte[] buffer);

    [LibraryImport("libc", EntryPoint = "fchmodat", SetLastError = true)]
    private static partial int ChmodAt(int dirFd, byte[] path, uint mode, int flags);

    private const int CurrentDirectory = -100; // AT_FDCWD
    private const int NoFollow = 0x100; // AT_SYMLINK_NOFOLLOW
    private const uint BasicStats = 0x7ff; // STATX_BASIC_STATS
    private const int ModeOffset = 28; // stx_mode
    private const int FileTypeMask = 0xF000;
    private const int LinkType = 0xA000;
    private const int FolderType = 0x4000;
    private const int PermissionMask = 0xFFF;
    private const int NameOffset = 19; // struct dirent on Linux: d_ino (8), d_off (8), d_reclen (2), d_type (1), d_name

    internal static byte[] Terminated(string path) => [.. Encoding.UTF8.GetBytes(path), 0];

    /// <summary>The entries directly in <paramref name="folder"/>, each judged by lstat.</summary>
    internal static List<Entry> Children(byte[] folder)
    {
        var names = new List<byte[]>();
        var dir = OpenDir(folder);
        if (dir == 0) throw Failure("listing", folder);
        try
        {
            while (ReadDir(dir) is var entry && entry != 0)
            {
                var name = new List<byte>();
                for (var i = NameOffset; Marshal.ReadByte(entry, i) is var b && b != 0; i++) name.Add(b);
                if (name.SequenceEqual("."u8.ToArray()) || name.SequenceEqual(".."u8.ToArray())) continue;
                names.Add([.. name]);
            }
        }
        finally
        {
            CloseDir(dir);
        }

        // The directory handle is closed before the caller descends, so depth does not hold handles open.
        var parent = folder[..^1];
        return names.Select(name => Lstat([.. parent, (byte)'/', .. name, 0])).ToList();
    }

    /// <summary>Sets the permission bits of the entry itself; a link is refused, never followed.</summary>
    internal static void Chmod(byte[] path, UnixFileMode mode)
    {
        if (ChmodAt(CurrentDirectory, path, (uint)mode, NoFollow) != 0) throw Failure("setting the mode of", path);
    }

    private static Entry Lstat(byte[] path)
    {
        var buffer = new byte[256];
        if (StatX(CurrentDirectory, path, NoFollow, BasicStats, buffer) != 0) throw Failure("reading", path);
        var bits = BitConverter.ToUInt16(buffer, ModeOffset);
        var kind = (bits & FileTypeMask) switch
        {
            LinkType => Kind.Link,
            FolderType => Kind.Folder,
            _ => Kind.Other,
        };
        return new Entry(path, kind, (UnixFileMode)(bits & PermissionMask));
    }

    private static FileExtractionException Failure(string action, byte[] path) =>
        new($"Failed {action} `{EntryNames.Printable(Encoding.UTF8.GetString(path[..^1]))}`: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
}
