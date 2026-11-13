using System.Globalization;

namespace SilverModManager.Sdk.Diagnostics;

/// <summary>
/// Names the biggest anonymous mappings of the process when its native memory has grown past a threshold, so the install log
/// shows which allocator's arenas hold it. Reading /proc/self/smaps walks every page table of the process and stalls the
/// threads touching them, so it runs rarely and only above the threshold; the line says how long the read took.
/// </summary>
public sealed class SmapsDump(
    TimeProvider time,
    long thresholdBytes = 4L << 30,
    TimeSpan? minimumGap = null,
    Func<string>? readRollup = null,
    Func<string>? readSmaps = null)
{
    private const int Largest = 10;

    private readonly TimeSpan _gap = minimumGap ?? TimeSpan.FromMinutes(5);
    private readonly Func<string> _readRollup = readRollup ?? (() => File.ReadAllText("/proc/self/smaps_rollup"));
    private readonly Func<string> _readSmaps = readSmaps ?? (() => File.ReadAllText("/proc/self/smaps"));

    private long? _lastDump;

    /// <summary>
    /// The dump text when the anonymous memory is more than <c>threshold</c> above what the GC has committed and the last dump
    /// is old enough; null otherwise, and null when the kernel's files cannot be read.
    /// </summary>
    public string? TryDump(MemorySample sample)
    {
        if (sample.RssAnonBytes is not { } anon || anon - sample.CommittedHeapBytes <= thresholdBytes) return null;
        if (_lastDump is { } last && time.GetElapsedTime(last) < _gap) return null;

        var started = time.GetTimestamp();
        string rollup, smaps;
        try
        {
            rollup = _readRollup();
            smaps = _readSmaps();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not Linux, or /proc is hidden: nothing to dump, and not worth retrying every report.
            _lastDump = time.GetTimestamp();
            return null;
        }

        var took = time.GetElapsedTime(started);
        _lastDump = time.GetTimestamp();

        var mappings = string.Join(", ", LargestAnonymous(smaps, Largest).Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.Label} {m.Range} {m.AnonBytes / (1024.0 * 1024 * 1024):F2} GB")));
        var summary = string.Join(" ", rollup.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.StartsWith("Anonymous:", StringComparison.Ordinal) || l.StartsWith("Rss:", StringComparison.Ordinal)).Select(l => string.Join(' ', l.Split(' ', StringSplitOptions.RemoveEmptyEntries))));
        return string.Create(CultureInfo.InvariantCulture, $"largest anonymous mappings: {mappings}; rollup: {(summary.Length > 0 ? summary : rollup.Trim())}; read took {(long)took.TotalMilliseconds} ms");
    }

    /// <summary>The mappings of a /proc smaps text with the most anonymous bytes, biggest first. A mapping without a path is labelled <c>[anon]</c>.</summary>
    internal static IReadOnlyList<(string Range, string Label, long AnonBytes)> LargestAnonymous(string smapsText, int count = Largest)
    {
        var result = new List<(string Range, string Label, long AnonBytes)>();
        string? range = null, label = null;
        foreach (var line in smapsText.Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) continue;

            if (!fields[0].EndsWith(':'))
            {
                // A mapping header: range, permissions, offset, device, inode, then an optional path that may contain spaces.
                range = fields[0];
                label = fields.Length > 5 ? string.Join(' ', fields.Skip(5)) : "[anon]";
            }
            else if (fields[0] == "Anonymous:" && range is not null && fields.Length >= 2
                     && long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kb) && kb > 0)
            {
                result.Add((range, label ?? "[anon]", kb * 1024));
            }
        }
        return result.OrderByDescending(m => m.AnonBytes).Take(count).ToList();
    }
}
