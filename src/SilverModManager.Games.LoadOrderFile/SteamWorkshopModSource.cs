using NexusMods.Paths;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// The mods a Steam user subscribed to: <c>steamapps/workshop/content/&lt;app id&gt;/&lt;item id&gt;</c> beside the
/// game's <c>steamapps/common</c> folder, each holding a file with <paramref name="descriptor"/>'s extension that names
/// the mod. Found only for an install in a library laid out that way.
/// </summary>
public sealed class SteamWorkshopModSource(uint appId, Extension descriptor) : IExternalModSource
{
    public IReadOnlyList<string> Find(AbsolutePath gameFolder)
    {
        // Plain strings: a game folder at the top of a drive has no parents to ask for.
        var common = Path.GetDirectoryName(gameFolder.GetFullPath());
        if (common is null || !Path.GetFileName(common).Equals("common", StringComparison.OrdinalIgnoreCase)) return [];

        var library = Path.GetDirectoryName(common);
        if (library is null || !Path.GetFileName(library).Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return [];

        var content = gameFolder.FileSystem.FromUnsanitizedFullPath(library).Combine("workshop/content").Combine(appId.ToString());
        if (!content.DirectoryExists()) return [];

        return content.EnumerateDirectories(recursive: false)
            .OrderBy(item => item.FileName, StringComparer.Ordinal)
            .SelectMany(item => item.EnumerateFiles("*", recursive: false)
                .Where(file => file.FileName.EndsWith(descriptor.ToString(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.FileName, StringComparer.Ordinal)
                .Select(file => Path.GetFileNameWithoutExtension(file.FileName)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
