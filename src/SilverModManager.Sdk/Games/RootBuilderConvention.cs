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
}
