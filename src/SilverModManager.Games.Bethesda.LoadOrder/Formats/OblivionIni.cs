// Follows libloadorder src/ini.rs (use_my_games_directory).
namespace NexusMods.Games.Bethesda.LoadOrder.Formats;

/// <summary>The one Oblivion.ini setting the plugin list depends on.</summary>
public static class OblivionIni
{
    /// <summary>
    /// Whether the game keeps Plugins.txt in the user's AppData folder. It does unless <c>[General]</c> sets
    /// <c>bUseMyGamesDirectory</c> to 0, in which case the file sits in the game folder.
    /// </summary>
    public static bool UsesMyGamesDirectory(string iniText)
    {
        var inGeneral = false;
        foreach (var raw in iniText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;

            if (line[0] == '[')
            {
                inGeneral = line.TrimEnd(']').Substring(1).Equals("General", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inGeneral) continue;
            var equals = line.IndexOf('=');
            if (equals < 0) continue;
            if (line[..equals].Trim().Equals("bUseMyGamesDirectory", StringComparison.OrdinalIgnoreCase))
                return line[(equals + 1)..].Trim() != "0";
        }

        return true;
    }
}
