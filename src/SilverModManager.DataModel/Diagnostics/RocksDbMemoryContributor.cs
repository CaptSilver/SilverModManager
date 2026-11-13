using SilverModManager.MnemonicDB.Storage.Abstractions;
using SilverModManager.MnemonicDB.Storage.RocksDbBackend;
using SilverModManager.Sdk.Diagnostics;

namespace SilverModManager.DataModel.Diagnostics;

/// <summary>
/// Reports the RocksDB store's memtables, table readers and block cache to the install log, so the native remainder of the
/// process's memory can be split between the store and everything else.
/// </summary>
public sealed class RocksDbMemoryContributor(IStoreBackend backend) : IMemoryContributor
{
    private readonly Lazy<string?> _note = new(() =>
    {
        try { return MappedLibrary(File.ReadAllText("/proc/self/maps")) is { } library ? "rocksdb: " + library : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    });

    public string Name => "rocksdb";

    /// <summary>Which RocksDB build the process mapped: the jemalloc one allocates differently from the glibc one, and the log should say which it was.</summary>
    public string? Note => _note.Value;

    public IReadOnlyList<(string Key, long Bytes)> Sample()
    {
        if ((backend as Backend)?.Sample() is not { } report) return [];
        return
        [
            ("rocksdb-memtables", report.MemTables),
            ("rocksdb-readers", report.TableReaders),
            ("rocksdb-cache", report.BlockCacheUsage),
            ("rocksdb-cache-pinned", report.BlockCachePinned),
            ("rocksdb-cache-capacity", report.BlockCacheCapacity),
        ];
    }

    /// <summary>The file name of the first <c>librocksdb*.so</c> a /proc maps text lists, or null.</summary>
    internal static string? MappedLibrary(string mapsText)
    {
        foreach (var line in mapsText.Split('\n'))
        {
            var slash = line.LastIndexOf('/');
            if (slash < 0) continue;

            var name = line[(slash + 1)..].Trim();
            if (name.StartsWith("librocksdb", StringComparison.Ordinal) && name.Contains(".so", StringComparison.Ordinal)) return name;
        }
        return null;
    }
}
