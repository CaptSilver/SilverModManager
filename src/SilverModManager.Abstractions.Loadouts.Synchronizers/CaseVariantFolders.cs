using System.Runtime.InteropServices;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Finds sibling folders whose names differ only in letter case. Wine resolves names case-insensitively, so of
/// such a pair the game sees one folder and the other's files are lost to it.
/// </summary>
public static class CaseVariantFolders
{
    /// <summary>
    /// Whether the game is a Windows build, which on Linux always runs through Wine or Proton. Wine sees only one of
    /// two folders that differ in case; a native Linux game keeps them apart, so only Wine games need this reported.
    /// </summary>
    public static bool RunsUnderWine(GameInstallation installation)
        => installation.LocatorResult.WinePrefixPath is not null
           || installation.LocatorResult.Platform == OSPlatform.Windows;

    /// <summary>
    /// Every pair of sibling folders, among the folders the <paramref name="files"/> sit in, that differ only in case.
    /// Three spellings of one name give three pairs. A folder is a sibling of another only under the same parent
    /// spelling, so <c>mods/x</c> and <c>Mods/x</c> are not a pair: the pair is <c>mods</c> and <c>Mods</c>.
    /// </summary>
    public static IReadOnlyList<(GamePath First, GamePath Second)> FindAll(IEnumerable<GamePath> files)
        => FindAll(files, []);

    /// <summary>
    /// <see cref="FindAll(IEnumerable{GamePath})"/>, plus the <paramref name="folders"/> given themselves. Folders are
    /// needed because paths compare without regard to case: two files with the same relative name in two spellings
    /// of a folder are one path to the loadout and the sync tree, so only the folders on disk show both.
    /// </summary>
    public static IReadOnlyList<(GamePath First, GamePath Second)> FindAll(IEnumerable<GamePath> files, IEnumerable<GamePath> folders)
    {
        // Keyed by the parent as written and the name with case ignored; the value holds each spelling of the name.
        var spellings = new Dictionary<(LocationId Location, string Parent, string Name), SortedSet<string>>();
        foreach (var (path, isFolder) in files.Select(f => (f, false)).Concat(folders.Select(f => (f, true))))
        {
            var segments = path.Path.ToString().Split('/');
            var parent = "";
            for (var i = 0; i < segments.Length - (isFolder ? 0 : 1); i++)
            {
                var key = (path.LocationId, parent, segments[i].ToUpperInvariant());
                if (!spellings.TryGetValue(key, out var set)) spellings[key] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(segments[i]);
                parent = parent.Length == 0 ? segments[i] : parent + "/" + segments[i];
            }
        }

        var pairs = new List<(GamePath, GamePath)>();
        foreach (var ((location, parent, _), set) in spellings.OrderBy(kv => kv.Key.Location.ToString(), StringComparer.Ordinal).ThenBy(kv => kv.Key.Parent, StringComparer.Ordinal).ThenBy(kv => kv.Key.Name, StringComparer.Ordinal))
        {
            var names = set.ToArray();
            for (var i = 0; i < names.Length; i++)
            {
                for (var j = i + 1; j < names.Length; j++)
                    pairs.Add((new GamePath(location, Join(parent, names[i])), new GamePath(location, Join(parent, names[j]))));
            }
        }
        return pairs;
    }

    private static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;
}
