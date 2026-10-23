// Follows esplugin src/plugin.rs (is_master_file, is_light_plugin, is_medium_plugin, is_update_plugin, is_blueprint_plugin).
namespace SilverModManager.Games.Bethesda.LoadOrder;

/// <summary>
/// Per-game reading of a header's flags. esplugin branches on the game in each of these, so the
/// per-game facts come in as booleans and the game classes pass their own.
/// </summary>
public static class PluginFlags
{
    private const uint MasterFlag = 0x1;
    private const uint StarfieldLightFlag = 0x100;
    private const uint LightFlag = 0x200;
    private const uint StarfieldUpdateFlag = 0x200;
    private const uint MediumFlag = 0x400;
    private const uint BlueprintFlag = 0x800;

    /// <param name="masterByExtension">The game treats .esm and .esl files as masters whatever their flags say.</param>
    /// <param name="masterFlagIgnored">The game never reads a master flag (OpenMW, Oblivion Remastered); always false.</param>
    public static bool IsMaster(PluginHeader header, bool masterByExtension, bool masterFlagIgnored)
    {
        if (masterFlagIgnored) return false;
        if ((header.RecordFlags & MasterFlag) != 0) return true;
        return masterByExtension && Extension(header.FileName) is ".esm" or ".esl";
    }

    public static bool IsLight(PluginHeader header, bool supportsLightPlugins, bool isStarfield)
    {
        if (!supportsLightPlugins) return false;

        var flag = isStarfield ? StarfieldLightFlag : LightFlag;
        if ((header.RecordFlags & flag) != 0) return true;

        // On Starfield the update flag stops the .esl extension from forcing the light flag at load.
        if (isStarfield && IsUpdateFlagSet(header)) return false;
        return Extension(header.FileName) == ".esl";
    }

    public static bool IsMedium(PluginHeader header, bool supportsLightPlugins, bool isStarfield)
        => IsMediumFlagSet(header, isStarfield) && !IsLight(header, supportsLightPlugins, isStarfield);

    /// <summary>The game clears the update flag on a plugin with no masters or with the light or medium flag set.</summary>
    public static bool IsUpdate(PluginHeader header, bool isStarfield)
        => isStarfield
           && IsUpdateFlagSet(header)
           && (header.RecordFlags & StarfieldLightFlag) == 0
           && !IsMediumFlagSet(header, isStarfield)
           && header.Masters.Count > 0;

    public static bool IsBlueprint(PluginHeader header, bool isStarfield)
        => isStarfield && (header.RecordFlags & BlueprintFlag) != 0;

    private static bool IsUpdateFlagSet(PluginHeader header) => (header.RecordFlags & StarfieldUpdateFlag) != 0;

    private static bool IsMediumFlagSet(PluginHeader header, bool isStarfield)
        => isStarfield && (header.RecordFlags & MediumFlag) != 0;

    /// <summary>Lower-cased extension of the name with a trailing .ghost removed first.</summary>
    internal static string Extension(string fileName)
    {
        const string ghost = ".ghost";
        if (fileName.EndsWith(ghost, StringComparison.OrdinalIgnoreCase)) fileName = fileName[..^ghost.Length];
        return Path.GetExtension(fileName).ToLowerInvariant();
    }
}
