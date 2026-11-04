using System.Runtime.InteropServices;
using System.Text;

namespace SilverModManager.Backend.FileExtractor.Extractors;

/// <summary>The device and inode of a symbolic link itself, which .NET does not expose.</summary>
internal static partial class LinkInode
{
    [LibraryImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static partial int Lstat(byte[] path, byte[] buffer);

    // struct stat starts with st_dev then st_ino, both 8 bytes, on x86-64 and arm64 Linux.
    private const int Size = 512;

    /// <summary>The device and inode of <paramref name="path"/> without following it, or nulls when lstat is unavailable or fails.</summary>
    internal static (ulong? Device, ulong? Inode) Read(string path)
    {
        try
        {
            var buffer = new byte[Size];
            if (Lstat([.. Encoding.UTF8.GetBytes(path), 0], buffer) != 0) return (null, null);
            return (BitConverter.ToUInt64(buffer, 0), BitConverter.ToUInt64(buffer, 8));
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            return (null, null);
        }
    }
}
