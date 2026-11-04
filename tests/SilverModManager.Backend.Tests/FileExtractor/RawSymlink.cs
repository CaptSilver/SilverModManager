using System.Runtime.InteropServices;
using System.Text;
using SilverModManager.Backend.FileExtractor.Extractors;

namespace SilverModManager.Backend.Tests.FileExtractor;

/// <summary>Symbolic links with names that are not valid UTF-8, which .NET cannot create or list.</summary>
internal static partial class RawSymlink
{
    [LibraryImport("libc", EntryPoint = "symlink", SetLastError = true)]
    private static partial int Symlink(byte[] target, byte[] path);

    [LibraryImport("libc", EntryPoint = "readlink", SetLastError = true)]
    private static partial nint ReadLink(byte[] path, byte[] buffer, nuint size);

    [LibraryImport("libc", EntryPoint = "opendir", SetLastError = true)]
    private static partial nint OpenDir(byte[] path);

    [LibraryImport("libc", EntryPoint = "readdir")]
    private static partial nint ReadDir(nint dir);

    [LibraryImport("libc", EntryPoint = "closedir")]
    private static partial int CloseDir(nint dir);

    [LibraryImport("libc", EntryPoint = "unlink", SetLastError = true)]
    private static partial int Unlink(byte[] path);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    private static partial int Open(byte[] path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static partial int MkDir(byte[] path, uint mode);

    [LibraryImport("libc", EntryPoint = "rmdir", SetLastError = true)]
    private static partial int RmDir(byte[] path);

    private static byte[] Terminated(ReadOnlySpan<byte> bytes) => [.. bytes, 0];

    /// <summary>The NUL-terminated path of <paramref name="rawName"/> inside <paramref name="folder"/>.</summary>
    internal static byte[] RawPath(string folder, ReadOnlySpan<byte> rawName) => Terminated([.. Encoding.UTF8.GetBytes(folder), (byte)'/', .. rawName]);

    internal static void Create(string folder, byte[] rawName, string target)
    {
        var path = RawPath(folder, rawName);
        if (Symlink(Terminated(Encoding.UTF8.GetBytes(target)), path) != 0)
        {
            throw new IOException($"symlink failed with errno {Marshal.GetLastPInvokeError()}");
        }
    }

    /// <summary>Creates an empty file named <paramref name="rawName"/> with <paramref name="mode"/>; the umask can only remove bits, so tests pass modes it keeps.</summary>
    internal static void CreateFile(string folder, byte[] rawName, UnixFileMode mode)
    {
        const int createExclusive = 0x40 | 0x80; // O_CREAT | O_EXCL
        var fd = Open(RawPath(folder, rawName), createExclusive | 1, (uint)mode); // O_WRONLY
        if (fd < 0) throw new IOException($"open failed with errno {Marshal.GetLastPInvokeError()}");
        Close(fd);
    }

    /// <summary>Creates a folder named <paramref name="rawName"/> with <paramref name="mode"/>.</summary>
    internal static void CreateFolder(string folder, byte[] rawName, UnixFileMode mode)
    {
        if (MkDir(RawPath(folder, rawName), (uint)mode) != 0) throw new IOException($"mkdir failed with errno {Marshal.GetLastPInvokeError()}");
    }

    /// <summary>Removes the file, link or empty folder <paramref name="rawName"/>; the name may hold <c>/</c> to reach into raw-named folders.</summary>
    internal static void Remove(string folder, byte[] rawName)
    {
        var path = RawPath(folder, rawName);
        if (Unlink(path) != 0) RmDir(path);
    }

    /// <summary>The permission bits of <paramref name="rawName"/> itself, never of what it links to.</summary>
    internal static UnixFileMode ModeOf(string folder, byte[] rawName) =>
        RawStat.Lstat(RawPath(folder, rawName))?.Mode ?? throw new IOException($"lstat failed with errno {Marshal.GetLastPInvokeError()}");

    /// <summary>Whether <paramref name="rawName"/> exists as an entry of its own; a dangling link counts.</summary>
    internal static bool Exists(string folder, byte[] rawName) => RawStat.Lstat(RawPath(folder, rawName)) is not null;

    /// <summary>The target text of every symbolic link directly in <paramref name="folder"/>, whatever its name's bytes.</summary>
    internal static List<string> Targets(string folder) => Links(folder).Select(link => link.Target).ToList();

    /// <summary>Removes every symbolic link directly in <paramref name="folder"/>, so the folder can be deleted by a .NET cleanup.</summary>
    internal static void RemoveAll(string folder)
    {
        foreach (var (name, _) in Links(folder)) Unlink(RawPath(folder, name));
    }

    private static List<(byte[] Name, string Target)> Links(string folder)
    {
        var targets = new List<(byte[], string)>();
        var dir = OpenDir(Terminated(Encoding.UTF8.GetBytes(folder)));
        if (dir == 0) throw new IOException($"opendir failed with errno {Marshal.GetLastPInvokeError()}");
        try
        {
            // struct dirent on Linux: d_ino (8), d_off (8), d_reclen (2), d_type (1), d_name.
            while (ReadDir(dir) is var entry && entry != 0)
            {
                if (Marshal.ReadByte(entry, 18) != 10) continue; // DT_LNK
                var name = new List<byte>();
                for (var i = 19; Marshal.ReadByte(entry, i) is var b && b != 0; i++) name.Add(b);
                var buffer = new byte[4096];
                var length = ReadLink(RawPath(folder, name.ToArray()), buffer, (nuint)buffer.Length - 1);
                if (length > 0) targets.Add(([.. name], Encoding.UTF8.GetString(buffer, 0, (int)length)));
            }
        }
        finally
        {
            CloseDir(dir);
        }

        return targets;
    }
}
