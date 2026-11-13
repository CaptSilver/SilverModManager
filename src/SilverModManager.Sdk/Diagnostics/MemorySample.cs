using System.Globalization;

namespace SilverModManager.Sdk.Diagnostics;

/// <summary>
/// What a process holds at one moment. The managed heap alone cannot tell a leak in .NET objects from native memory
/// (archive readers, the store), so the kernel's anonymous and shared counts come with it where the OS gives them.
/// <paramref name="CommittedHeapBytes"/> is what the GC keeps from the OS, live objects or not: the gap between it and
/// the anonymous count is native memory. <paramref name="Native"/> splits that remainder by owner where a contributor can
/// tell (the C allocator's counts, RocksDB, Skia); <paramref name="NativeNotes"/> carries free text such as the mapped RocksDB library.
/// </summary>
public readonly record struct MemorySample(long ManagedHeapBytes, long CommittedHeapBytes, int Gen2Collections, long? RssAnonBytes, long? RssShmemBytes,
    IReadOnlyList<(string Key, long Bytes)>? Native = null, IReadOnlyList<string>? NativeNotes = null)
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

    /// <summary>
    /// The native breakdown on one line such as <c>malloc-used 4.9 GB (rocksdb-readers 3.1 GB, skia 0.5 GB, other 1.2 GB), malloc-free 9.8 GB</c>,
    /// or an empty string when no contributor reported. Counts that sit inside malloc's used bytes (RocksDB's memtables, readers and block cache,
    /// Skia's live rasters) print inside its parentheses with the unattributed rest as <c>other</c>. The allocator's arena and mmap
    /// totals, the pinned part of the block cache, capacities and peaks follow as plain counts: arena and mmap growing while
    /// malloc-used stays flat is memory the allocator keeps, not memory anything still uses.
    /// </summary>
    public string DescribeNative()
    {
        if (Native is not { Count: > 0 }) return "";

        var parts = new List<string>();
        var used = Native.Where(n => n.Key == "malloc-used").Select(n => (long?)n.Bytes).FirstOrDefault();
        var inside = Native.Where(n => IsInsideMallocUsed(n.Key)).ToList();
        if (used is { } usedBytes)
        {
            var attributed = inside.Select(n => $"{n.Key} {Gigabytes(n.Bytes)}").ToList();
            attributed.Add($"other {Gigabytes(Math.Max(0, usedBytes - inside.Sum(n => n.Bytes)))}");
            parts.Add($"malloc-used {Gigabytes(usedBytes)} ({string.Join(", ", attributed)})");
        }
        else
        {
            parts.AddRange(inside.Select(n => $"{n.Key} {Gigabytes(n.Bytes)}"));
        }

        parts.AddRange(Native.Where(n => n.Key == "malloc-free").Select(n => $"{n.Key} {Gigabytes(n.Bytes)}"));
        parts.AddRange(Native.Where(n => !IsInsideMallocUsed(n.Key) && n.Key is not ("malloc-used" or "malloc-free")).Select(n => $"{n.Key} {Gigabytes(n.Bytes)}"));
        if (NativeNotes is not null) parts.AddRange(NativeNotes);
        return string.Join(", ", parts);
    }

    // The pinned block-cache bytes are a part of rocksdb-cache, so counting them inside as well would take them off "other" twice.
    private static bool IsInsideMallocUsed(string key)
        => key is not ("rocksdb-cache-capacity" or "rocksdb-cache-pinned" or "skia-peak") && (key.StartsWith("rocksdb-", StringComparison.Ordinal) || key.StartsWith("skia", StringComparison.Ordinal));

    private static string Gigabytes(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / BytesPerGb:F1} GB");
}
