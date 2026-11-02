using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using NexusMods.Archives.Nx.Headers.Managed;
using NexusMods.Archives.Nx.Structs;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;

namespace SilverModManager.DataModel;

/// <summary>One Nx archive in the store: where it is and which files it holds.</summary>
internal sealed record ArchiveContents(AbsolutePath ArchivePath, Size Size, DateTimeOffset LastModified, FrozenDictionary<Hash, FileEntry> Entries);

/// <summary>
/// Which archive holds each stored file, as an immutable snapshot that a reader can use without a lock. The bulk sits
/// in frozen dictionaries, which are quick to read but cost a full copy to change; archives added since go in a small
/// persistent overlay instead, so adding one costs in proportion to what it holds and not to everything the store
/// holds. The overlay is folded into new frozen dictionaries once it has grown to a fraction of the bulk, which keeps
/// the copying amortised over many additions.
/// </summary>
internal sealed class ArchiveIndex
{
    // A store with few files folds often, but a fold of a small bulk is cheap.
    private const int MinOverlayEntriesBeforeFold = 32 * 1024;
    private const int BulkPerOverlayEntryBeforeFold = 4;

    public static readonly ArchiveIndex Empty = new(
        FrozenDictionary<Hash, ArchiveContents>.Empty, FrozenDictionary<AbsolutePath, ArchiveContents>.Empty,
        ImmutableDictionary<Hash, ArchiveContents>.Empty, ImmutableDictionary<AbsolutePath, ArchiveContents>.Empty);

    private readonly FrozenDictionary<Hash, ArchiveContents> _bulkByEntry;
    private readonly FrozenDictionary<AbsolutePath, ArchiveContents> _bulkArchives;
    private readonly ImmutableDictionary<Hash, ArchiveContents> _recentByEntry;
    private readonly ImmutableDictionary<AbsolutePath, ArchiveContents> _recentArchives;

    private ArchiveIndex(
        FrozenDictionary<Hash, ArchiveContents> bulkByEntry, FrozenDictionary<AbsolutePath, ArchiveContents> bulkArchives,
        ImmutableDictionary<Hash, ArchiveContents> recentByEntry, ImmutableDictionary<AbsolutePath, ArchiveContents> recentArchives)
    {
        _bulkByEntry = bulkByEntry;
        _bulkArchives = bulkArchives;
        _recentByEntry = recentByEntry;
        _recentArchives = recentArchives;
    }

    /// <summary>An index of exactly <paramref name="archives"/>; a file in more than one is attributed to the last of them.</summary>
    public static ArchiveIndex Of(IEnumerable<ArchiveContents> archives)
    {
        var byPath = new Dictionary<AbsolutePath, ArchiveContents>();
        var byEntry = new Dictionary<Hash, ArchiveContents>();
        foreach (var archive in archives)
        {
            byPath[archive.ArchivePath] = archive;
            // The indexer, not Add: a file can sit in two archives, which is harmless until a collection removes one.
            foreach (var entry in archive.Entries)
                byEntry[entry.Key] = archive;
        }
        return new ArchiveIndex(byEntry.ToFrozenDictionary(), byPath.ToFrozenDictionary(),
            ImmutableDictionary<Hash, ArchiveContents>.Empty, ImmutableDictionary<AbsolutePath, ArchiveContents>.Empty);
    }

    public bool ContainsFile(Hash hash) => _recentByEntry.ContainsKey(hash) || _bulkByEntry.ContainsKey(hash);

    public bool TryGetArchiveOf(Hash hash, [MaybeNullWhen(false)] out ArchiveContents archive)
        => _recentByEntry.TryGetValue(hash, out archive) || _bulkByEntry.TryGetValue(hash, out archive);

    public bool TryGetArchive(AbsolutePath path, [MaybeNullWhen(false)] out ArchiveContents archive)
        => _recentArchives.TryGetValue(path, out archive) || _bulkArchives.TryGetValue(path, out archive);

    /// <summary>This index and <paramref name="archive"/>, whose files count as stored in it over any other archive's.</summary>
    public ArchiveIndex With(ArchiveContents archive)
    {
        var recentArchives = _recentArchives.SetItem(archive.ArchivePath, archive);
        var recentByEntry = _recentByEntry.SetItems(archive.Entries.Select(e => new KeyValuePair<Hash, ArchiveContents>(e.Key, archive)));
        var added = new ArchiveIndex(_bulkByEntry, _bulkArchives, recentByEntry, recentArchives);
        return recentByEntry.Count >= Math.Max(MinOverlayEntriesBeforeFold, _bulkByEntry.Count / BulkPerOverlayEntryBeforeFold)
            ? added.Folded()
            : added;
    }

    private ArchiveIndex Folded()
    {
        var byEntry = new Dictionary<Hash, ArchiveContents>(_bulkByEntry);
        foreach (var (hash, archive) in _recentByEntry) byEntry[hash] = archive;
        var byPath = new Dictionary<AbsolutePath, ArchiveContents>(_bulkArchives);
        foreach (var (path, archive) in _recentArchives) byPath[path] = archive;
        return new ArchiveIndex(byEntry.ToFrozenDictionary(), byPath.ToFrozenDictionary(),
            ImmutableDictionary<Hash, ArchiveContents>.Empty, ImmutableDictionary<AbsolutePath, ArchiveContents>.Empty);
    }
}
