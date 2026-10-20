using System.Text;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.Paths;
using NexusMods.Sdk.IO;

namespace NexusMods.Games.TestFramework;

/// <summary>
/// Renders a directory tree as text so a test can pin exactly what is on disk.
/// </summary>
public static class DirectoryDescription
{
    /// <summary>
    /// One line per file, sorted ordinally by relative path:
    /// <c>path TAB size TAB xxHash3 (16 lowercase hex) TAB x|-</c>.
    /// The last column is the user execute bit; group and other bits depend on the umask,
    /// so they are left out. The size and the hash are those of the content behind a symlink.
    /// </summary>
    public static async Task<string> Describe(AbsolutePath root)
    {
        var lines = new List<(string Path, string Line)>();
        foreach (var file in root.EnumerateFiles(recursive: true))
        {
            var relative = file.RelativeTo(root).ToString().Replace('\\', '/');
            var size = FileFingerprint.Of(file).Size.Value;
            var hash = await file.XxHash3Async();
            var executable = file.GetUnixFileMode().HasFlag(UnixFileMode.UserExecute) ? 'x' : '-';
            lines.Add((relative, $"{relative}\t{size}\t{hash.Value:x16}\t{executable}"));
        }

        lines.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        var sb = new StringBuilder();
        foreach (var (_, line) in lines) sb.Append(line).Append('\n');
        return sb.ToString();
    }
}
