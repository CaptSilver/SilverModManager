using System.Globalization;

namespace SilverModManager.MnemonicDB.Storage.RocksDbBackend;

/// <summary>
/// What an open RocksDB database holds in memory, in bytes, read from its own properties. The block cache is shared by every
/// reader, so <see cref="BlockCacheUsage"/> is not part of <see cref="TableReaders"/>; <see cref="BlockCacheCapacity"/> is
/// the ceiling, not a use.
/// </summary>
public sealed record RocksDbMemoryReport(
    long MemTables, long TableReaders, long BlockCacheUsage, long BlockCachePinned, long BlockCacheCapacity, long ApproximateTotal)
{
    /// <summary>A RocksDB property value as a number; zero when the property is missing or not an integer.</summary>
    public static long ParseProperty(string? text)
        => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
