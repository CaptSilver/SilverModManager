using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.FolderCase;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.LoadoutPage;

/// <summary>
/// Finds the loadout items that deploy a file whose game path contains some text. Callers run it off the UI thread:
/// a search may read every file path of the loadout. A search checks its token between mods and before it rebuilds
/// anything, and throws <see cref="OperationCanceledException"/> once the token is cancelled.
/// </summary>
public interface IModFileSearch
{
    /// <summary>
    /// The items that hold a file matching <paramref name="text"/> (ignoring case), and every group above them, so a
    /// parent row stays when only a child matches.
    /// </summary>
    IReadOnlySet<EntityId> ItemsWithFileMatching(string text, CancellationToken cancellationToken);
}

/// <summary>
/// The file paths of a loadout, held per owning group as one text so a search is a substring scan per mod rather than
/// per file. The index is built from a snapshot and does not change; <see cref="LoadoutFileSearch"/> rebuilds it when
/// the database moves.
/// </summary>
public sealed class ModFileSearchIndex : IModFileSearch
{
    private const char Separator = '\n';

    private readonly (EntityId Owner, string Paths, EntityId[] Ancestors)[] _owners;

    /// <param name="files">Every file path with the group that holds it.</param>
    /// <param name="parentOf">The group above a group, or none for one at the top.</param>
    public ModFileSearchIndex(IEnumerable<OwnedFilePath> files, Func<EntityId, EntityId?> parentOf)
    {
        var byOwner = new Dictionary<EntityId, System.Text.StringBuilder>();
        foreach (var (owner, path) in files)
        {
            if (byOwner.TryGetValue(owner, out var paths) is false)
                byOwner[owner] = paths = new System.Text.StringBuilder();
            paths.Append(path).Append(Separator);
        }

        _owners = byOwner
            .Select(pair => (pair.Key, pair.Value.ToString(), AncestorsOf(pair.Key, parentOf)))
            .ToArray();
    }

    /// <summary>Reads the loadout's file paths and builds the index from them.</summary>
    public static ModFileSearchIndex Read(IDb db, LoadoutId loadout) =>
        new(LoadoutFilePaths.Read(db, loadout), owner => ParentOf(db, owner));

    private static EntityId? ParentOf(IDb db, EntityId item)
    {
        var loadoutItem = LoadoutItem.Load(db, item);
        return loadoutItem.IsValid() && loadoutItem.HasParent() ? loadoutItem.ParentId.Value : null;
    }

    private static EntityId[] AncestorsOf(EntityId owner, Func<EntityId, EntityId?> parentOf)
    {
        var ancestors = new List<EntityId>();
        // The depth bound stops a cycle in bad data from looping.
        for (var parent = parentOf(owner); parent is { } id && ancestors.Count < 64; parent = parentOf(id))
            ancestors.Add(id);
        return ancestors.ToArray();
    }

    /// <summary>How many groups hold at least one file. The index keeps one text per group, whatever the file count.</summary>
    public int OwnerCount => _owners.Length;

    public IReadOnlySet<EntityId> ItemsWithFileMatching(string text, CancellationToken cancellationToken)
    {
        var found = new HashSet<EntityId>();
        // A path never holds the separator, so text that does can only match across two files.
        if (text.Length == 0 || text.Contains(Separator)) return found;

        foreach (var (owner, paths, ancestors) in _owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (paths.Contains(text, StringComparison.OrdinalIgnoreCase) is false) continue;
            found.Add(owner);
            found.UnionWith(ancestors);
        }

        return found;
    }
}

/// <summary>
/// <see cref="IModFileSearch"/> over a loadout in the database. The index is built on the first search and kept while
/// the database stays at the same transaction, so typing a word rebuilds nothing. A database that keeps committing
/// (an install) would otherwise rebuild the whole index on every pause in typing, so a built index is reused for
/// <paramref name="minRebuildInterval"/> even after the database has moved on.
/// </summary>
public sealed class LoadoutFileSearch(IConnection connection, LoadoutId loadout, TimeSpan? minRebuildInterval = null) : IModFileSearch
{
    /// <summary>How long a built index is reused while the database keeps changing.</summary>
    public static readonly TimeSpan DefaultMinRebuildInterval = TimeSpan.FromSeconds(3);

    private readonly TimeSpan _minRebuildInterval = minRebuildInterval ?? DefaultMinRebuildInterval;
    private readonly object _gate = new();
    private (TxId Basis, long BuiltAt, ModFileSearchIndex Index)? _cached;

    public IReadOnlySet<EntityId> ItemsWithFileMatching(string text, CancellationToken cancellationToken) =>
        IndexFor(cancellationToken).ItemsWithFileMatching(text, cancellationToken);

    private ModFileSearchIndex IndexFor(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // A search that was replaced while it waited for the lock must not start another rebuild.
            cancellationToken.ThrowIfCancellationRequested();

            var db = connection.Db;
            if (_cached is { } cached && (cached.Basis == db.BasisTxId || System.Diagnostics.Stopwatch.GetElapsedTime(cached.BuiltAt) < _minRebuildInterval))
                return cached.Index;

            var index = ModFileSearchIndex.Read(db, loadout);
            _cached = (db.BasisTxId, System.Diagnostics.Stopwatch.GetTimestamp(), index);
            return index;
        }
    }
}

/// <summary>
/// One <see cref="LoadoutFileSearch"/> per loadout, shared by every page open on it, so the loadout page and its
/// collection pages hold one index between them rather than a copy each. The cache holds a search only weakly: the
/// index goes away with the last page that uses it.
/// </summary>
public sealed class LoadoutFileSearches(IConnection connection)
{
    private readonly Dictionary<LoadoutId, WeakReference<LoadoutFileSearch>> _searches = new();

    public IModFileSearch For(LoadoutId loadout)
    {
        lock (_searches)
        {
            if (_searches.TryGetValue(loadout, out var weak) && weak.TryGetTarget(out var existing))
                return existing;

            var created = new LoadoutFileSearch(connection, loadout);
            _searches[loadout] = new WeakReference<LoadoutFileSearch>(created);
            return created;
        }
    }
}
