using System.Text;

namespace SilverModManager.Backend.FileExtractor.Extractors;

/// <summary>The device and inode of a symbolic link itself, which .NET does not expose.</summary>
internal static class LinkInode
{
    /// <summary>The device and inode of <paramref name="path"/> without following it, or nulls when lstat is unavailable or fails.</summary>
    internal static (ulong? Device, ulong? Inode) Read(string path)
    {
        try
        {
            return RawStat.Lstat([.. Encoding.UTF8.GetBytes(path), 0]) is { } info ? (info.Device, info.Inode) : (null, null);
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            return (null, null);
        }
    }
}
