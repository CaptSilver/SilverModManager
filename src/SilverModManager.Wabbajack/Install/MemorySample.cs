using System.Globalization;

namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// What a process holds at one moment. The managed heap alone cannot tell a leak in .NET objects from native memory
/// (archive readers, the store), so the kernel's anonymous and shared counts come with it where the OS gives them.
/// <paramref name="CommittedHeapBytes"/> is what the GC keeps from the OS, live objects or not: the gap between it and
/// the anonymous count is native memory.
/// </summary>
public readonly record struct MemorySample(long ManagedHeapBytes, long CommittedHeapBytes, int Gen2Collections, long? RssAnonBytes, long? RssShmemBytes)
{
    private const double BytesPerGb = 1024.0 * 1024 * 1024;

    /// <summary>One line such as <c>heap 12.3 GB (committed 20.0 GB), gen2 45, anon 30.1 GB, shmem 0.1 GB</c>; counts the OS did not give are left out.</summary>
    public string Describe()
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"heap {ManagedHeapBytes / BytesPerGb:F1} GB (committed {CommittedHeapBytes / BytesPerGb:F1} GB), gen2 {Gen2Collections}");
        if (RssAnonBytes is { } anon) text += string.Create(CultureInfo.InvariantCulture, $", anon {anon / BytesPerGb:F1} GB");
        if (RssShmemBytes is { } shmem) text += string.Create(CultureInfo.InvariantCulture, $", shmem {shmem / BytesPerGb:F1} GB");
        return text;
    }
}

public interface IMemorySampler
{
    MemorySample Sample();
}

/// <summary>Reads this process's managed heap from the GC and its anonymous and shared resident memory from /proc/self/status.</summary>
public sealed class ProcessMemorySampler : IMemorySampler
{
    private const string StatusPath = "/proc/self/status";

    public MemorySample Sample()
    {
        var (anon, shmem) = ReadStatus();
        return new MemorySample(GC.GetTotalMemory(false), GC.GetGCMemoryInfo().TotalCommittedBytes, GC.CollectionCount(2), anon, shmem);
    }

    private static (long? Anon, long? Shmem) ReadStatus()
    {
        try
        {
            return ParseStatus(File.ReadAllText(StatusPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not Linux, or /proc is hidden: the sample still carries the heap.
            return (null, null);
        }
    }

    /// <summary>Finds the <c>RssAnon</c> and <c>RssShmem</c> lines (kB) of a /proc status text and returns them in bytes.</summary>
    internal static (long? Anon, long? Shmem) ParseStatus(string text)
    {
        long? anon = null, shmem = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("RssAnon:", StringComparison.Ordinal)) anon = Kilobytes(line);
            else if (line.StartsWith("RssShmem:", StringComparison.Ordinal)) shmem = Kilobytes(line);
        }
        return (anon, shmem);
    }

    private static long? Kilobytes(string line)
    {
        var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2 && long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kb) ? kb * 1024 : null;
    }
}
