using System.Runtime.InteropServices;
using System.Text;

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

    private static byte[] Terminated(ReadOnlySpan<byte> bytes) => [.. bytes, 0];

    internal static void Create(string folder, byte[] rawName, string target)
    {
        var path = Terminated([.. Encoding.UTF8.GetBytes(folder), (byte)'/', .. rawName]);
        if (Symlink(Terminated(Encoding.UTF8.GetBytes(target)), path) != 0)
        {
            throw new IOException($"symlink failed with errno {Marshal.GetLastPInvokeError()}");
        }
    }

    /// <summary>The target text of every symbolic link directly in <paramref name="folder"/>, whatever its name's bytes.</summary>
    internal static List<string> Targets(string folder) => Links(folder).Select(link => link.Target).ToList();

    /// <summary>Removes every symbolic link directly in <paramref name="folder"/>, so the folder can be deleted by a .NET cleanup.</summary>
    internal static void RemoveAll(string folder)
    {
        foreach (var (name, _) in Links(folder)) Unlink(Terminated([.. Encoding.UTF8.GetBytes(folder), (byte)'/', .. name]));
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
                var length = ReadLink(Terminated([.. Encoding.UTF8.GetBytes(folder), (byte)'/', .. name]), buffer, (nuint)buffer.Length - 1);
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
