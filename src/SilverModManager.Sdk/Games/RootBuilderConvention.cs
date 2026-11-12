using NexusMods.Paths;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// Root Builder's packaging rule: a folder named "root" at the top of a mod holds files meant for the game
/// folder, not for the game's mod folder (Data). Installers and the Wabbajack importer share this one check.
/// </summary>
public static class RootBuilderConvention
{
    public const string FolderName = "root";

    /// <summary>
    /// True when <paramref name="modPath"/> is a file inside the mod's root folder; <paramref name="underGameFolder"/>
    /// is then its path below that folder. A file that is itself called "root" has no folder to step into and is not matched.
    /// </summary>
    public static bool TryMap(RelativePath modPath, out RelativePath underGameFolder)
    {
        var parts = modPath.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && parts[0].Equals(FolderName, StringComparison.OrdinalIgnoreCase))
        {
            underGameFolder = RelativePath.FromUnsanitizedInput(string.Join('/', parts.Skip(1)));
            return true;
        }

        underGameFolder = default;
        return false;
    }

    /// <summary>
    /// Like <see cref="TryMap(RelativePath, out RelativePath)"/>, but also finds a root folder one level down, inside a
    /// top folder named in <paramref name="wrapperFolders"/> (a wrapper around the mod, or a folder named after the game).
    /// </summary>
    public static bool TryMapBelowWrapper(RelativePath modPath, IReadOnlyCollection<string> wrapperFolders, out RelativePath underGameFolder)
    {
        if (TryMap(modPath, out underGameFolder))
            return true;

        var parts = modPath.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 2 && wrapperFolders.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
            return TryMap(RelativePath.FromUnsanitizedInput(string.Join('/', parts.Skip(1))), out underGameFolder);

        underGameFolder = default;
        return false;
    }

    /// <summary>
    /// The top folder every file shares, when it is a wrapper around the mod and not one of the game's own folders
    /// (<paramref name="gameFolders"/>, e.g. Data and the content folders such as meshes); otherwise <c>null</c>.
    /// </summary>
    public static string? CommonWrapperFolder(IEnumerable<RelativePath> modPaths, IReadOnlyCollection<string> gameFolders)
    {
        string? common = null;
        foreach (var path in modPaths)
        {
            var parts = path.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return null;
            if (common is null)
                common = parts[0];
            else if (!common.Equals(parts[0], StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return common is not null && !gameFolders.Contains(common, StringComparer.OrdinalIgnoreCase) ? common : null;
    }
}
