using System.Globalization;

namespace SilverModManager.Wabbajack.Tests.Install;

/// <summary>
/// The system-wide <c>Shmem</c> count from /proc/meminfo. tmpfs pages (spool files under /tmp) are not in the process's
/// anonymous or resident counts, so a benchmark that only reads the process cannot see them.
/// </summary>
internal static class MeminfoShmem
{
    /// <summary>Reads /proc/meminfo; null when it is missing or has no Shmem line.</summary>
    public static long? Read()
    {
        try
        {
            return Parse(File.ReadAllText("/proc/meminfo"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The <c>Shmem:</c> line of a meminfo text (kB) in bytes. <c>ShmemHugePages</c> and <c>ShmemPmdMapped</c> are other lines and do not match.</summary>
    internal static long? Parse(string meminfoText)
    {
        foreach (var line in meminfoText.Split('\n'))
        {
            if (!line.StartsWith("Shmem:", StringComparison.Ordinal)) continue;
            var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            return fields.Length >= 2 && long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kb) ? kb * 1024 : null;
        }
        return null;
    }
}
