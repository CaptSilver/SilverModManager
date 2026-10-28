using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Witcher.Witcher3.SortOrder;

/// <summary>How the Load Order page names the folders of the game's Mods directory.</summary>
public static class WitcherModFolders
{
    /// <summary>
    /// The folder directly under Mods that a file lies in, or null for any other path (including a file placed
    /// straight into Mods, which belongs to no folder).
    /// </summary>
    public static string? FolderOf(GamePath path)
    {
        if (path.LocationId != LocationId.Game) return null;

        var parts = path.Path.Path.Split('/');
        if (parts.Length < 3) return null;
        return parts[0].Equals(WitcherConstants.W3ModsFolder, StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
    }

    /// <summary>The Witcher 3 reads folder names without regard to case, so the key is the lower-cased name.</summary>
    public static SortItemKey<string> KeyFor(string folder) => new(folder.ToLowerInvariant());

    /// <summary>The folder the game reads first, which holds the merged script files.</summary>
    public static SortItemKey<string> MergedKey { get; } = KeyFor(WitcherConstants.W3MergedFolder);
}
