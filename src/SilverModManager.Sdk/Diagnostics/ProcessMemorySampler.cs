using System.Globalization;

namespace SilverModManager.Sdk.Diagnostics;

/// <summary>
/// Reads this process's managed heap from the GC and its anonymous and shared resident memory from /proc/self/status, and
/// asks each <see cref="IMemoryContributor"/> what part of the native remainder it holds.
/// </summary>
public sealed class ProcessMemorySampler : IMemorySampler
{
    private const string StatusPath = "/proc/self/status";

    private readonly IMemoryContributor[] _contributors;

    public ProcessMemorySampler() : this([])
    {
    }

    public ProcessMemorySampler(IEnumerable<IMemoryContributor> contributors) => _contributors = contributors.ToArray();

    public MemorySample Sample()
    {
        var (anon, shmem) = ReadStatus();
        var native = new List<(string Key, long Bytes)>();
        var notes = new List<string>();
        foreach (var contributor in _contributors)
        {
            native.AddRange(contributor.Sample());
            if (contributor.Note is { } note && !notes.Contains(note)) notes.Add(note);
        }

        return new MemorySample(GC.GetTotalMemory(false), GC.GetGCMemoryInfo().TotalCommittedBytes, GC.CollectionCount(2), anon, shmem,
            native.Count > 0 ? native : null, notes.Count > 0 ? notes : null);
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
