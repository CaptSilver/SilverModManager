using System.Text;
using SilverModManager.Deployment.Fuse.Native;

namespace SilverModManager.Deployment.Fuse.Kernel;

/// <summary>Reads <c>/proc/self/mountinfo</c> to see whether the kernel lists a mount.</summary>
internal static class MountTable
{
    internal const string Path = "/proc/self/mountinfo";

    /// <param name="realMountPoint">The mount point with symlinks resolved, the way the kernel lists it.</param>
    /// <param name="fileSystemType">e.g. <c>fuse.silvermod</c>.</param>
    internal static bool IsMounted(string realMountPoint, string fileSystemType) =>
        File.ReadLines(Path).Any(line => Matches(line, realMountPoint, fileSystemType));

    /// <summary>
    /// For a mount point given as its parent's real path and its own name. Resolving only the parent keeps the
    /// name as the kernel lists it; resolving the whole path would swap a mount point that is itself a link for
    /// its target.
    /// </summary>
    internal static bool IsMounted(string parentRealPath, string leaf, string fileSystemType) =>
        File.ReadLines(Path).Any(line => Matches(line, parentRealPath, leaf, fileSystemType));

    internal static bool Matches(string line, string parentRealPath, string leaf, string fileSystemType) =>
        Matches(line, Join(parentRealPath, leaf), fileSystemType);

    /// <summary>The real path of the parent and the name of the last component, which is kept verbatim.</summary>
    internal static (string Parent, string Leaf) SplitForLookup(string mountPoint)
    {
        var trimmed = mountPoint.Length > 1 ? mountPoint.TrimEnd('/') : mountPoint;
        var split = trimmed.LastIndexOf('/');
        if (split < 0) return (LibcProcess.RealPath("."), trimmed);
        var parent = split == 0 ? "/" : trimmed[..split];
        return (LibcProcess.RealPath(parent), trimmed[(split + 1)..]);
    }

    private static string Join(string parent, string leaf) => parent == "/" ? "/" + leaf : $"{parent}/{leaf}";

    // id parent major:minor root mountpoint options [optional fields...] - fstype source superoptions
    internal static bool Matches(string line, string realMountPoint, string fileSystemType)
    {
        var fields = line.Split(' ');
        var separator = Array.IndexOf(fields, "-");
        if (fields.Length < 7 || separator < 6 || separator + 1 >= fields.Length) return false;
        return fields[separator + 1] == fileSystemType && Unescape(fields[4]) == realMountPoint;
    }

    // The kernel writes space, tab, newline and backslash as three-digit octal escapes.
    private static string Unescape(string field)
    {
        if (!field.Contains('\\')) return field;

        var bytes = new List<byte>(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && IsOctal(field, i + 1))
            {
                bytes.Add((byte)Convert.ToInt32(field.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(field[i].ToString()));
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static bool IsOctal(string field, int start) =>
        start + 3 <= field.Length && field.AsSpan(start, 3).IndexOfAnyExceptInRange('0', '7') < 0;
}
