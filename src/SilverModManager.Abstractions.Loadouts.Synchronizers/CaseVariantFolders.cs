using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Finds two folders under one parent whose names differ only in letter case. Wine resolves names
/// case-insensitively, so of such a pair the game sees one folder and the other's files are lost to it.
/// </summary>
internal static class CaseVariantFolders
{
    /// <summary>The first pair found, or null when every folder has one spelling.</summary>
    public static (GamePath First, GamePath Second)? Find(IEnumerable<GamePath> files)
    {
        // Keyed by the folder's path with case ignored; the value is the spelling seen first.
        var seen = new Dictionary<(LocationId, string), string>();
        foreach (var file in files)
        {
            var segments = file.Path.ToString().Split('/');
            var folder = "";
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var parent = folder;
                folder = parent.Length == 0 ? segments[i] : parent + "/" + segments[i];
                var key = (file.LocationId, folder.ToUpperInvariant());
                if (seen.TryAdd(key, folder)) continue;
                if (seen[key] != folder) return (new GamePath(file.LocationId, seen[key]), new GamePath(file.LocationId, folder));
            }
        }
        return null;
    }
}
