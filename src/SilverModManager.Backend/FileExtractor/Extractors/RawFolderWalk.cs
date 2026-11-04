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
    /// <param name="Path">NUL-terminated bytes of the entry's path.</param>
    /// <param name="Kind">What lstat says the entry itself is, never what a link points at.</param>
    /// <param name="Mode">The permission bits of the entry itself.</param>
    internal readonly record struct Entry(byte[] Path, RawStat.Kind Kind, UnixFileMode Mode);

    [LibraryImport("libc", EntryPoint = "opendir", SetLastError = true)]
    private static partial nint OpenDir(byte[] path);

    [LibraryImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static partial nint ReadDir(nint dir);

    [LibraryImport("libc", EntryPoint = "closedir")]
    private static partial int CloseDir(nint dir);

    [LibraryImport("libc", EntryPoint = "fchmodat", SetLastError = true)]
    private static partial int ChmodAt(int dirFd, byte[] path, uint mode, int flags);

    [LibraryImport("libc", EntryPoint = "unlink", SetLastError = true)]
    private static partial int UnlinkPath(byte[] path);

    private const int CurrentDirectory = -100; // AT_FDCWD
    private const int NoFollow = 0x100; // AT_SYMLINK_NOFOLLOW
    private const int NotSupported = 95; // ENOTSUP, also EOPNOTSUPP on Linux
    private const int NotImplemented = 38; // ENOSYS
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
            while (true)
            {
                // readdir returns NULL both at the end and on an error, and only errno tells them apart.
                Marshal.SetLastPInvokeError(0);
                var entry = ReadDir(dir);
                if (entry == 0)
                {
                    if (Marshal.GetLastPInvokeError() != 0) throw Failure("listing", folder);
                    break;
                }

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
        return names.Select(name => Describe([.. parent, (byte)'/', .. name, 0])).ToList();
    }

    /// <summary>
    /// Sets the permission bits of an entry already judged not to be a link. The call refuses a link rather than
    /// following one; glibc older than 2.32, or without /proc on a kernel before 6.6, cannot do that and
    /// reports it as unsupported, so the plain call takes over for the entry the caller has just judged.
    /// </summary>
    internal static void Chmod(byte[] path, UnixFileMode mode)
    {
        if (ChmodAt(CurrentDirectory, path, (uint)mode, NoFollow) == 0) return;
        if (!NoFollowUnsupported(Marshal.GetLastPInvokeError())) throw Failure("setting the mode of", path);
        if (ChmodAt(CurrentDirectory, path, (uint)mode, 0) != 0) throw Failure("setting the mode of", path);
    }

    internal static bool NoFollowUnsupported(int errno) => errno is NotSupported or NotImplemented;

    /// <summary>Removes the file or link at <paramref name="path"/>; a link is removed itself, never followed.</summary>
    internal static void Unlink(byte[] path)
    {
        if (UnlinkPath(path) != 0) throw Failure("removing", path);
    }

    private static Entry Describe(byte[] path)
    {
        var info = RawStat.Lstat(path) ?? throw Failure("reading", path);
        return new Entry(path, info.Kind, info.Mode);
    }

    private static FileExtractionException Failure(string action, byte[] path) =>
        new($"Failed {action} `{EntryNames.Printable(Encoding.UTF8.GetString(path[..^1]))}`: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
}
