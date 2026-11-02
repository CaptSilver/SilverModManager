using System.Collections.Concurrent;
using SilverModManager.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using NexusMods.Paths;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>
/// Says whether a library archive is complete for a list. Holding the archive is not enough: a library that
/// imported it before nested archives kept their own bytes has the rows for the nested archive but nothing in the
/// file store, so a directive that copies the nested archive whole has nothing to copy. The archive then has to be
/// fetched or added again, which stores those bytes under the identity the existing rows already name.
/// </summary>
public sealed class HeldArchiveCheck
{
    /// <summary>Asks nothing of any archive; for a plan that was not built from a list.</summary>
    public static readonly HeldArchiveCheck NothingNeeded = new(null, new Dictionary<FileIdentity, RelativePath[][]>());

    private readonly IFileStore? _store;
    // Per archive, each distinct path through nested archives that some directive reads as a file.
    private readonly Dictionary<FileIdentity, RelativePath[][]> _sources;

    // One index per archive for as long as the check lives: an archive's entry rows are written with it and never change.
    private readonly ConcurrentDictionary<EntityId, Lazy<Dictionary<string, LibraryArchiveFileEntry.ReadOnly>>> _indexes = new();
    private int _indexesBuilt;

    private HeldArchiveCheck(IFileStore? store, Dictionary<FileIdentity, RelativePath[][]> sources)
    {
        _store = store;
        _sources = sources;
    }

    /// <summary>How many entry indexes the checks have built, one per archive that had a path followed through it.</summary>
    public int IndexesBuilt => Volatile.Read(ref _indexesBuilt);

    /// <summary>
    /// Collects the paths the list reads inside archives once, so each check only walks the paths of its own archive.
    /// A path matters only when it ends on a file the library would have expanded: anything else keeps no bytes of its
    /// own to lack, and most archives have no such path, so they are never indexed.
    /// </summary>
    public static HeldArchiveCheck For(ModList list, IFileStore store)
    {
        var sources = new Dictionary<FileIdentity, Dictionary<string, RelativePath[]>>();
        foreach (var directive in list.Directives)
        {
            if (directive is not FromArchive { Source.Parts.Count: > 0 } from || !NestedArchives.MayBeExpanded(from.Source.Parts[^1])) continue;

            if (!sources.TryGetValue(from.Source.ArchiveXxHash64, out var paths))
                sources[from.Source.ArchiveXxHash64] = paths = [];
            paths.TryAdd(string.Join('/', from.Source.Parts.Select(LibraryArchiveIndex.Fold)), [.. from.Source.Parts]);
        }

        return new HeldArchiveCheck(store, sources.ToDictionary(pair => pair.Key, pair => pair.Value.Values.ToArray()));
    }

    /// <summary>
    /// Whether <paramref name="held"/>, the library's copy of the list's archive <paramref name="archive"/>, has the
    /// bytes of every nested archive the list copies whole. An archive no directive reads from is complete.
    /// </summary>
    public bool IsComplete(IDb db, FileIdentity archive, LibraryFile.ReadOnly held)
    {
        if (_store is not { } store || !_sources.TryGetValue(archive, out var paths)) return true;
        if (!held.TryGetAsLibraryArchive(out var top)) return true;

        foreach (var parts in paths)
        {
            if (CopiesAnExpandedArchiveWhole(db, store, top, parts)) return false;
        }
        return true;
    }

    /// <summary>
    /// Follows <paramref name="parts"/> through the library's expanded archives the way the path resolver does, and
    /// says whether it ends on an expanded archive that has no bytes in the store.
    /// </summary>
    private bool CopiesAnExpandedArchiveWhole(IDb db, IFileStore store, LibraryArchive.ReadOnly top, RelativePath[] parts)
    {
        var current = top;
        for (var i = 0; i < parts.Length; i++)
        {
            var index = IndexOf(db, current);

            // Whatever the path leaves the library for (an archive kind it does not expand, a missing entry) is not something fetching again fixes.
            if (!index.TryGetValue(LibraryArchiveIndex.Fold(parts[i]), out var entry)) return false;
            var file = entry.AsLibraryFile();
            if (!file.TryGetAsLibraryArchive(out var next)) return false;

            if (i == parts.Length - 1) return !HaveBytes(store, file);
            current = next;
        }
        return false;
    }

    private Dictionary<string, LibraryArchiveFileEntry.ReadOnly> IndexOf(IDb db, LibraryArchive.ReadOnly archive)
        => _indexes.GetOrAdd(archive.Id, _ => new Lazy<Dictionary<string, LibraryArchiveFileEntry.ReadOnly>>(() =>
        {
            Interlocked.Increment(ref _indexesBuilt);
            return LibraryArchiveIndex.Build(db, archive);
        })).Value;

    // The store answers from its in-memory index, so the wait is over at once; callers here are not async.
    private static bool HaveBytes(IFileStore store, LibraryFile.ReadOnly file) => store.HaveFile(file.Identity).AsTask().GetAwaiter().GetResult();
}
