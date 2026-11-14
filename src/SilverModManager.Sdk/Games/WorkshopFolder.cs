using JetBrains.Annotations;
using NexusMods.Paths;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// One subscribed Steam Workshop item on disk: its published file id and the folder Steam keeps it in.
/// </summary>
[PublicAPI]
public readonly record struct WorkshopItem(ulong PublishedFileId, AbsolutePath Folder);

/// <summary>
/// Steam's Workshop content folder of a game, <c>steamapps/workshop/content/&lt;app id&gt;</c>, which sits beside the
/// game's <c>steamapps/common</c> folder and outside every location the app manages.
/// </summary>
[PublicAPI]
public static class WorkshopFolder
{
    /// <summary>
    /// The content folder for the app, or null when the game is not in a Steam library laid out as
    /// <c>steamapps/common/&lt;game&gt;</c> or the library has no such folder.
    /// </summary>
    public static AbsolutePath? Content(AbsolutePath gameFolder, uint appId)
    {
        // Plain strings: a game folder at the top of a drive has no parents to ask for.
        var common = Path.GetDirectoryName(gameFolder.GetFullPath());
        if (common is null || !Path.GetFileName(common).Equals("common", StringComparison.OrdinalIgnoreCase)) return null;

        var library = Path.GetDirectoryName(common);
        if (library is null || !Path.GetFileName(library).Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return null;

        var content = gameFolder.FileSystem.FromUnsanitizedFullPath(library).Combine("workshop/content").Combine(appId.ToString());
        return content.DirectoryExists() ? content : null;
    }

    /// <summary>
    /// The subscribed items, ordered by folder name. A folder whose name is not a number is not an item: Steam names
    /// them by published file id, so anything else was put there by hand.
    /// </summary>
    public static IReadOnlyList<WorkshopItem> Items(AbsolutePath gameFolder, uint appId)
    {
        if (Content(gameFolder, appId) is not { } content) return [];

        return content.EnumerateDirectories(recursive: false)
            .Select(folder => ulong.TryParse(folder.FileName, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? (WorkshopItem?)new WorkshopItem(id, folder) : null)
            .OfType<WorkshopItem>()
            .OrderBy(item => item.Folder.FileName, StringComparer.Ordinal)
            .ToList();
    }
}
