using System.Text;

namespace NexusMods.Deployment.Fuse.Kernel;

/// <summary>Reads <c>/proc/self/mountinfo</c> to see whether the kernel lists a mount.</summary>
internal static class MountTable
{
    internal const string Path = "/proc/self/mountinfo";

    /// <param name="realMountPoint">The mount point with symlinks resolved, the way the kernel lists it.</param>
    /// <param name="fileSystemType">e.g. <c>fuse.silvermod</c>.</param>
    internal static bool IsMounted(string realMountPoint, string fileSystemType) =>
        File.ReadLines(Path).Any(line => Matches(line, realMountPoint, fileSystemType));

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
