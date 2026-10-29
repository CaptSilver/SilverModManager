using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Finds two folders under one parent whose names differ only in letter case. Wine resolves names
/// case-insensitively, so of such a pair the game sees one folder and the other's files are lost to it.
/// </summary>
internal static class CaseVariantFolders
{
    /// <summary>
    /// The first pair found, or null when every folder has one spelling. A pair is a folder of <paramref name="deployed"/>
    /// beside another folder of <paramref name="deployed"/> or of <paramref name="onDisk"/>; two folders of
    /// <paramref name="onDisk"/> alone are not this apply's doing and are not reported.
    /// </summary>
    public static (GamePath First, GamePath Second)? Find(IEnumerable<GamePath> deployed, IEnumerable<GamePath> onDisk)
    {
        // Keyed by the folder's path with case ignored; the value is the spelling seen first.
        var seen = new Dictionary<(LocationId, string), string>();
        foreach (var file in deployed)
        {
            foreach (var (folder, key) in FoldersOf(file))
            {
                if (seen.TryAdd(key, folder)) continue;
                if (seen[key] != folder) return (new GamePath(file.LocationId, seen[key]), new GamePath(file.LocationId, folder));
            }
        }

        foreach (var file in onDisk)
        {
            foreach (var (folder, key) in FoldersOf(file))
            {
                if (seen.TryGetValue(key, out var spelling) && spelling != folder)
                    return (new GamePath(file.LocationId, folder), new GamePath(file.LocationId, spelling));
            }
        }
        return null;
    }

    private static IEnumerable<(string Folder, (LocationId, string) Key)> FoldersOf(GamePath file)
    {
        var segments = file.Path.ToString().Split('/');
        var folder = "";
        for (var i = 0; i < segments.Length - 1; i++)
        {
            folder = folder.Length == 0 ? segments[i] : folder + "/" + segments[i];
            yield return (folder, (file.LocationId, folder.ToUpperInvariant()));
        }
    }
}
