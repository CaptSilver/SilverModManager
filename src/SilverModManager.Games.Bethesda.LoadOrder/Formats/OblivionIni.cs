// Follows libloadorder src/ini.rs (use_my_games_directory).
using SilverModManager.Sdk.Ini;

namespace SilverModManager.Games.Bethesda.LoadOrder.Formats;

/// <summary>The one Oblivion.ini setting the plugin list depends on.</summary>
public static class OblivionIni
{
    /// <summary>
    /// Whether the game keeps Plugins.txt in the user's AppData folder. It does unless <c>[General]</c> sets
    /// <c>bUseMyGamesDirectory</c> to 0, in which case the file sits in the game folder.
    /// </summary>
    public static bool UsesMyGamesDirectory(string iniText) => UsesMyGamesDirectory(IniDocument.Parse(iniText));

    /// <inheritdoc cref="UsesMyGamesDirectory(string)"/>
    public static bool UsesMyGamesDirectory(IniDocument ini) =>
        ini.Get("General", "bUseMyGamesDirectory") != "0";
}
