using System.Collections.Frozen;
using System.Runtime.InteropServices;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.FolderCase;

/// <summary>What <see cref="FolderSpellings.Build"/> reads. Each source is walked once.</summary>
/// <param name="Fixed">Top folders of <see cref="LocationId.Game"/> that have one spelling whatever the disk or loadout holds.</param>
/// <param name="AllTiers">Whether every folder at any depth is folded. False folds only <paramref name="Fixed"/>.</param>
/// <param name="DiskFolders">Every directory on disk, filtered paths left out.</param>
/// <param name="DiskFiles">Recorded disk state. <c>Vanilla</c> is true when the row matches a file the game's store attests.</param>
/// <param name="LoadoutTargets">Every target path of the loadout, enabled or not.</param>
public readonly record struct FolderSpellingSources(
    IReadOnlyList<string> Fixed,
    bool AllTiers,
    IEnumerable<GamePath> DiskFolders,
    IEnumerable<(GamePath Path, bool Vanilla)> DiskFiles,
    IEnumerable<GamePath> LoadoutTargets);

/// <summary>
/// One spelling for every folder, whatever the paths of the loadout, the recorded disk state and the disk itself
/// call it. Paths compare without regard to case, so rows that differ only in case are one path to the C# side and two
/// to the SQL side, and a case-sensitive disk keeps the folders apart; the spelling chosen here is what the
/// disk, the rows and every write agree on.
/// </summary>
/// <remarks>
/// A folder is keyed by its whole path with case ignored, so resolving one never depends on another and the build is a
/// single pass over each source. Rules, first that applies:
/// <list type="number">
/// <item>A fixed spelling.</item>
/// <item>The on-disk spelling holding a file the store attests: Steam owns that spelling and verifying the game would
/// recreate the twin. Two such spellings make the group game-owned, which nothing here or in the merge changes.</item>
/// <item>A spelling on disk; with several, the one with the most files beneath it.</item>
/// <item>The loadout's spelling with the most files.</item>
/// <item>The ordinal smallest.</item>
/// </list>
/// A file's own name is folded the same way when every tier is on: the disk's spelling, then the most rows, then the
/// ordinal smallest.
/// </remarks>
public sealed class FolderSpellings
{
    private sealed class Candidate
    {
        public bool Vanilla;
        public bool OnDisk;
        public int DiskFiles;
        public int LoadoutFiles;
    }

    private sealed class Group
    {
        public readonly Dictionary<string, Candidate> Spellings = new(StringComparer.Ordinal);
        public string Resolved = "";
        public bool GameOwned;
    }

    private readonly FrozenDictionary<string, string> _fixed;
    private readonly Dictionary<LocationId, Dictionary<string, Group>> _folders = new();
    private readonly Dictionary<LocationId, Dictionary<string, Group>> _leaves = new();
    private readonly Dictionary<(LocationId, string), string> _folderCache = new();
    private int _cacheHits;

    private FolderSpellings(IReadOnlyList<string> fixedTopFolders)
        => _fixed = fixedTopFolders.ToFrozenDictionary(f => f, f => f, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether leaf names are folded too.</summary>
    private bool FoldLeaves { get; init; }

    /// <summary>How many <see cref="Canonical"/> calls were answered from the per-folder cache. Tests count this instead of timing.</summary>
    internal int CacheHits => _cacheHits;

    /// <summary>Folds only the given top folders of the game folder.</summary>
    public static FolderSpellings Fixed(IReadOnlyList<string> topFolders) => new(topFolders);

    /// <summary>A spelling for every folder the sources name.</summary>
    public static FolderSpellings Build(FolderSpellingSources sources)
    {
        var result = new FolderSpellings(sources.Fixed) { FoldLeaves = sources.AllTiers };
        if (!sources.AllTiers) return result;

        foreach (var folder in sources.DiskFolders) result.Add(folder, isFolder: true, onDisk: true, vanilla: false, counted: false);
        foreach (var (path, vanilla) in sources.DiskFiles) result.Add(path, isFolder: false, onDisk: true, vanilla, counted: true);
        foreach (var path in sources.LoadoutTargets) result.Add(path, isFolder: false, onDisk: false, vanilla: false, counted: true);
        result.Resolve();
        return result;
    }

    private void Add(GamePath path, bool isFolder, bool onDisk, bool vanilla, bool counted)
    {
        var text = path.Path.ToString();
        if (text.Length == 0) return;

        var folders = GroupsOf(_folders, path.LocationId);
        var lookup = folders.GetAlternateLookup<ReadOnlySpan<char>>();
        var span = text.AsSpan();
        var start = 0;
        while (start < span.Length)
        {
            var slash = span[start..].IndexOf('/');
            var isLeaf = slash < 0;
            var end = isLeaf ? span.Length : start + slash;
            if (isLeaf && !isFolder) break;

            Touch(lookup, folders, span[..end], span[start..end], onDisk, vanilla, counted);
            start = end + 1;
        }

        if (!isFolder && onDisk | counted)
        {
            var leaves = GroupsOf(_leaves, path.LocationId);
            var leafLookup = leaves.GetAlternateLookup<ReadOnlySpan<char>>();
            var name = span[(span.LastIndexOf('/') + 1)..];
            Touch(leafLookup, leaves, span, name, onDisk, vanilla, counted);
        }
    }

    private static void Touch(Dictionary<string, Group>.AlternateLookup<ReadOnlySpan<char>> lookup, Dictionary<string, Group> groups,
        ReadOnlySpan<char> key, ReadOnlySpan<char> spelling, bool onDisk, bool vanilla, bool counted)
    {
        if (!lookup.TryGetValue(key, out var group))
        {
            group = new Group();
            groups[key.ToString()] = group;
        }

        var spellingLookup = group.Spellings.GetAlternateLookup<ReadOnlySpan<char>>();
        if (!spellingLookup.TryGetValue(spelling, out var candidate))
        {
            candidate = new Candidate();
            group.Spellings[spelling.ToString()] = candidate;
        }

        candidate.OnDisk |= onDisk;
        candidate.Vanilla |= vanilla;
        if (!counted) return;
        if (onDisk) candidate.DiskFiles++;
        else candidate.LoadoutFiles++;
    }

    private static Dictionary<string, Group> GroupsOf(Dictionary<LocationId, Dictionary<string, Group>> all, LocationId location)
    {
        ref var groups = ref CollectionsMarshal.GetValueRefOrAddDefault(all, location, out _);
        return groups ??= new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
    }

    private void Resolve()
    {
        foreach (var group in _folders.Values.SelectMany(g => g.Values)) ResolveFolder(group);
        foreach (var group in _leaves.Values.SelectMany(g => g.Values)) ResolveLeaf(group);
    }

    private static void ResolveFolder(Group group)
    {
        if (group.Spellings.Count == 1)
        {
            group.Resolved = group.Spellings.Keys.First();
            return;
        }

        var vanilla = group.Spellings.Where(s => s.Value.Vanilla).Select(s => s.Key).Order(StringComparer.Ordinal).ToArray();
        if (vanilla.Length > 0)
        {
            group.Resolved = vanilla[0];
            group.GameOwned = vanilla.Length > 1;
            return;
        }

        var onDisk = group.Spellings.Where(s => s.Value.OnDisk).ToArray();
        if (onDisk.Length > 0)
        {
            group.Resolved = MostFiles(onDisk, c => c.DiskFiles);
            return;
        }

        group.Resolved = MostFiles(group.Spellings, c => c.LoadoutFiles);
    }

    private static void ResolveLeaf(Group group)
    {
        if (group.Spellings.Count == 1)
        {
            group.Resolved = group.Spellings.Keys.First();
            return;
        }

        var onDisk = group.Spellings.Where(s => s.Value.OnDisk).Select(s => s.Key).Order(StringComparer.Ordinal).ToArray();
        group.Resolved = onDisk.Length > 0 ? onDisk[0] : MostFiles(group.Spellings, c => c.LoadoutFiles);
    }

    private static string MostFiles(IEnumerable<KeyValuePair<string, Candidate>> candidates, Func<Candidate, int> count)
        => candidates.OrderByDescending(c => count(c.Value)).ThenBy(c => c.Key, StringComparer.Ordinal).First().Key;

    /// <summary>
    /// <paramref name="path"/> with every folder segment in its chosen spelling, and the file name too when leaf names
    /// are folded. A path no source named comes back as spelled.
    /// </summary>
    public GamePath Canonical(GamePath path)
    {
        var text = path.Path.ToString();
        var slash = text.LastIndexOf('/');
        var folder = slash < 0 ? "" : text[..slash];
        var leaf = slash < 0 ? text : text[(slash + 1)..];

        var canonicalFolder = CanonicalFolder(path.LocationId, folder);
        var canonicalLeaf = FoldLeaves ? CanonicalLeaf(path.LocationId, text, leaf) : leaf;
        if (canonicalFolder.Equals(folder, StringComparison.Ordinal) && canonicalLeaf.Equals(leaf, StringComparison.Ordinal)) return path;

        var joined = canonicalFolder.Length == 0 ? canonicalLeaf : canonicalFolder + "/" + canonicalLeaf;
        return new GamePath(path.LocationId, RelativePath.FromUnsanitizedInput(joined));
    }

    /// <summary><paramref name="folder"/> (a relative path, <c>""</c> for the location's root) with every segment in its chosen spelling.</summary>
    public string CanonicalFolder(LocationId location, string folder)
    {
        if (folder.Length == 0) return folder;
        if (_folderCache.TryGetValue((location, folder), out var cached))
        {
            _cacheHits++;
            return cached;
        }

        var folded = Fold(location, folder);
        _folderCache[(location, folder)] = folded;
        return folded;
    }

    private string Fold(LocationId location, string folder)
    {
        _folders.TryGetValue(location, out var groups);
        var builder = new System.Text.StringBuilder(folder.Length);
        var start = 0;
        while (start < folder.Length)
        {
            var slash = folder.IndexOf('/', start);
            var end = slash < 0 ? folder.Length : slash;
            var segment = folder[start..end];
            if (start == 0 && location == LocationId.Game && _fixed.TryGetValue(segment, out var fixedName)) segment = fixedName;
            else if (groups is not null && groups.TryGetValue(folder[..end], out var group) && !group.GameOwned) segment = group.Resolved;

            if (builder.Length > 0) builder.Append('/');
            builder.Append(segment);
            start = end + 1;
        }
        return builder.ToString();
    }

    private string CanonicalLeaf(LocationId location, string fullPath, string leaf)
        => _leaves.TryGetValue(location, out var groups) && groups.TryGetValue(fullPath, out var group) ? group.Resolved : leaf;

    /// <summary>Whether <paramref name="path"/> is already written in its chosen spelling, compared exactly.</summary>
    public bool IsCanonical(GamePath path) => Canonical(path).Path.ToString().Equals(path.Path.ToString(), StringComparison.Ordinal);

    /// <summary>Whether the folder is spelled two ways by the game's own files, which nothing here or in the merge changes.</summary>
    public bool IsGameOwned(LocationId location, string folder)
        => _folders.TryGetValue(location, out var groups) && groups.TryGetValue(folder, out var group) && group.GameOwned;

    /// <summary>Whether <paramref name="folder"/>'s first segment is one of the fixed top folders.</summary>
    internal bool IsFixedTop(LocationId location, string name) => location == LocationId.Game && _fixed.ContainsKey(name);
}
