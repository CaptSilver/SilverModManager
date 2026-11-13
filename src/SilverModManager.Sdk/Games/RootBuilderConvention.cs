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
    /// With <paramref name="aliasFolders"/> (folders named after the game), the root folder may also sit below one of them
    /// inside such a wrapper: Mod/Skyrim Special Edition/root/...
    /// </summary>
    public static bool TryMapBelowWrapper(RelativePath modPath, IReadOnlyCollection<string> wrapperFolders, out RelativePath underGameFolder, IReadOnlyCollection<string>? aliasFolders = null)
    {
        if (TryMap(modPath, out underGameFolder))
            return true;

        var parts = modPath.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 2 && wrapperFolders.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
        {
            if (TryMap(RelativePath.FromUnsanitizedInput(string.Join('/', parts.Skip(1))), out underGameFolder))
                return true;

            if (aliasFolders is not null && parts.Length > 3 && aliasFolders.Contains(parts[1], StringComparer.OrdinalIgnoreCase))
                return TryMap(RelativePath.FromUnsanitizedInput(string.Join('/', parts.Skip(2))), out underGameFolder);
        }

        underGameFolder = default;
        return false;
    }

    // Top folders that hold documentation or installer data, never files for the game.
    private static readonly string[] NonContentFolders = ["fomod", "docs", "documentation", "screenshots"];

    /// <summary>
    /// The top folder every file shares, when it is a wrapper around the mod and not one of the game's own folders
    /// (<paramref name="gameFolders"/>, e.g. Data and the content folders such as meshes); otherwise <c>null</c>.
    /// Loose files at the top (readme, screenshots) and folders that only hold documentation or installer data
    /// (fomod, docs) don't count against it.
    /// </summary>
    public static string? CommonWrapperFolder(IEnumerable<RelativePath> modPaths, IReadOnlyCollection<string> gameFolders)
    {
        string? common = null;
        foreach (var path in modPaths)
        {
            var parts = path.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || NonContentFolders.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
                continue;
            if (common is null)
                common = parts[0];
            else if (!common.Equals(parts[0], StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return common is not null && !gameFolders.Contains(common, StringComparer.OrdinalIgnoreCase) ? common : null;
    }

    /// <summary>
    /// The folder to step into below <paramref name="wrapper"/>: when every one of <paramref name="wrappedPaths"/> sits in
    /// a single folder named after the game (<paramref name="gameAliases"/>), as in ModName/Skyrim Special Edition/Data/...,
    /// that folder; otherwise <paramref name="wrapper"/> itself.
    /// </summary>
    public static RelativePath StepIntoGameFolder(RelativePath wrapper, IEnumerable<RelativePath> wrappedPaths, IReadOnlyCollection<string> gameAliases)
    {
        var inside = wrappedPaths.Select(p => p.RelativeTo(wrapper)).ToArray();
        if (inside.Length == 0 || inside.Any(p => p.Parts.Count() < 2))
            return wrapper;

        var tops = inside.Select(p => p.TopParent).Distinct().ToArray();
        return tops.Length == 1 && gameAliases.Contains(tops[0].ToString(), StringComparer.OrdinalIgnoreCase)
            ? wrapper / tops[0]
            : wrapper;
    }
}
