using System.Security.Cryptography;
using NexusMods.Paths;

namespace SilverModManager.Games.FromSoft.Tests.Fixtures;

/// <summary>
/// Everything observable about a folder: every entry's path, a symlink's target, a file's content hash and every
/// modification time. Two snapshots are equal only if nothing was written, replaced, relinked or touched.
/// </summary>
internal static class TreeSnapshot
{
    public static string[] Take(AbsolutePath root)
    {
        var entries = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(root.ToString(), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root.ToString(), path);
            var info = new FileInfo(path);
            if (info.LinkTarget is { } target) entries.Add($"{relative} -> {target}");
            else if (Directory.Exists(path)) entries.Add($"{relative}/ {Directory.GetLastWriteTimeUtc(path).Ticks}");
            else entries.Add($"{relative} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))} {info.LastWriteTimeUtc.Ticks}");
        }

        return entries.ToArray();
    }
}
