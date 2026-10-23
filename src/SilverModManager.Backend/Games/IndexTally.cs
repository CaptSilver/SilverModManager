using DynamicData.Kernel;
using Microsoft.Extensions.Logging;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;

namespace SilverModManager.Backend.Games;

/// <summary>
/// Why a file's hash could not be taken from the stored hash rows.
/// </summary>
internal enum StoredHashMiss
{
    /// <summary>No store has a row for the path.</summary>
    NoRowForPath,

    /// <summary>Every row for the path describes a file of another size.</summary>
    SizeDiffers,

    /// <summary>A row has the file's size but not its minimal hash, so the file is not the one the row describes.</summary>
    ContentDiffers,

    /// <summary>Matching rows in different stores carry different hashes.</summary>
    StoresDisagree,
}

internal readonly record struct StoredHashLookup(Optional<Hash> Hash, StoredHashMiss Miss)
{
    public static StoredHashLookup Found(Hash hash) => new(hash, default);
    public static StoredHashLookup Missed(StoredHashMiss miss) => new(Optional<Hash>.None, miss);
}

/// <summary>
/// Counts how each file of one index was settled, so the log says how much of the folder was really read and
/// why, not only that indexing took long.
/// </summary>
internal sealed class IndexTally
{
    private readonly long[] _misses = new long[Enum.GetValues<StoredHashMiss>().Length];
    private long _unchanged;
    private long _fromStoredHash;
    private long _bytesHashed;

    public void Unchanged() => Interlocked.Increment(ref _unchanged);
    public void FromStoredHash() => Interlocked.Increment(ref _fromStoredHash);

    public void FullyHashed(StoredHashMiss reason, Size size)
    {
        Interlocked.Increment(ref _misses[(int)reason]);
        Interlocked.Add(ref _bytesHashed, (long)size.Value);
    }

    public long UnchangedCount => Interlocked.Read(ref _unchanged);
    public long FromStoredHashCount => Interlocked.Read(ref _fromStoredHash);
    public long FullyHashedCount => _misses.Sum();
    public long MissCount(StoredHashMiss reason) => Interlocked.Read(ref _misses[(int)reason]);
    public Size BytesHashed => Size.From((ulong)Interlocked.Read(ref _bytesHashed));

    public void Log(ILogger logger, string game, int newFiles, int modifiedFiles, int removedFiles)
    {
        logger.LogInformation(
            "Indexed {Game}: {Unchanged} unchanged by size and time, {New} new, {Modified} modified, {Removed} removed. " +
            "{FromStored} hashes taken from stored rows; {Hashed} files ({Bytes}) read in full ({NoRow} without a row, {SizeDiffers} of another size, {ContentDiffers} with other content, {Disagree} with disagreeing stores)",
            game, UnchangedCount, newFiles, modifiedFiles, removedFiles,
            FromStoredHashCount, FullyHashedCount, BytesHashed,
            MissCount(StoredHashMiss.NoRowForPath), MissCount(StoredHashMiss.SizeDiffers), MissCount(StoredHashMiss.ContentDiffers), MissCount(StoredHashMiss.StoresDisagree));
    }
}
