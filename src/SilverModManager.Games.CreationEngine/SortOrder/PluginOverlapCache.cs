using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SilverModManager.Games.Bethesda.Archives;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.CreationEngine.SortOrder;

/// <summary>
/// What a plugin's records and an archive's files look like to the sorter, by content identity. Walking every
/// record header of a large load order is real I/O, but a file's records and listing only change when its bytes do,
/// so each is read once and every later sort is a dictionary read.
/// </summary>
/// <remarks>
/// A file that was tried and could not be read is cached as <c>null</c> so a broken one is not re-read on every
/// sort. A file the store has no bytes for is not cached: the store may have them next time.
/// </remarks>
internal sealed class PluginOverlapCache(IStreamSourceDispatcher streams, PluginFormat format, ILogger logger)
{
    private readonly ConcurrentDictionary<FileIdentity, Lazy<Task<Cached<PluginRecords>>>> _records = new();
    private readonly ConcurrentDictionary<FileIdentity, Lazy<Task<Cached<ArchiveAssetSet>>>> _assets = new();

    // A result is either a value, a verdict that the file is unusable, or a gap in the store that is not a verdict.
    private readonly record struct Cached<T>(T? Value, bool Final) where T : class;

    /// <summary>The plugin's records before they are resolved against its masters, or <c>null</c> when it cannot be read.</summary>
    public Task<PluginRecords?> Records(FileIdentity identity, string name) =>
        Load(_records, identity, async () =>
        {
            await using var stream = await streams.OpenAsync(identity);
            if (stream is null) return new Cached<PluginRecords>(null, false);

            try
            {
                // A store stream can be slow to read small pieces from; the walker reads headers and skips bodies.
                using var buffered = new BufferedStream(stream, 64 * 1024);
                return new Cached<PluginRecords>(PluginRecordWalker.Read(buffered, format, name), true);
            }
            catch (InvalidPluginException e)
            {
                logger.LogWarning("Not reading the records of {Plugin} for LOOT: {Reason}", name, e.Message);
                return new Cached<PluginRecords>(null, true);
            }
        });

    /// <summary>The files of the archive, or <c>null</c> when it cannot be read.</summary>
    public Task<ArchiveAssetSet?> Assets(FileIdentity identity, string name) =>
        Load(_assets, identity, async () =>
        {
            var stream = await streams.OpenAsync(identity);
            if (stream is null) return new Cached<ArchiveAssetSet>(null, false);

            try
            {
                return new Cached<ArchiveAssetSet>(await ArchiveAssetSet.Read(stream, CancellationToken.None), true);
            }
            catch (BethesdaArchiveException e)
            {
                // LOOT logs an archive it cannot read and carries on without it.
                logger.LogWarning("Not listing the files of the archive {Archive} for LOOT: {Reason}", name, e.Message);
                return new Cached<ArchiveAssetSet>(null, true);
            }
        });

    private static async Task<T?> Load<T>(
        ConcurrentDictionary<FileIdentity, Lazy<Task<Cached<T>>>> cache, FileIdentity identity, Func<Task<Cached<T>>> read)
        where T : class
    {
        var lazy = cache.GetOrAdd(identity, _ => new Lazy<Task<Cached<T>>>(read));
        try
        {
            var result = await lazy.Value;
            if (!result.Final) cache.TryRemove(KeyValuePair.Create(identity, lazy));
            return result.Value;
        }
        catch
        {
            // The store failing is not an answer about the file, so the next sort tries again.
            cache.TryRemove(KeyValuePair.Create(identity, lazy));
            throw;
        }
    }
}
