namespace SilverModManager.Games.CreationEngine.OpenMw;

/// <summary>
/// Reads the one switch of OpenMW's <c>settings.cfg</c> the app cares about. The file is the user's and is never
/// written.
/// </summary>
public static class OpenMwSettings
{
    // OpenMW's settings-default.cfg puts the key under [Groundcover] ("enable separate groundcover handling",
    // enabled = false). World::loadGroundcoverFiles returns at once while it is false.
    private const string GroundcoverSection = "Groundcover";
    private const string EnabledKey = "enabled";

    /// <summary>
    /// Whether the <c>enabled</c> key of the <c>[Groundcover]</c> section is true. A file with no such key, as OpenMW
    /// ships without one, leaves groundcover off.
    /// </summary>
    public static bool GroundcoverEnabled(string settingsText)
    {
        var inSection = false;
        var enabled = false;
        foreach (var rawLine in settingsText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                inSection = close > 0 && line[1..close].Trim().Equals(GroundcoverSection, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var equals = line.IndexOf('=');
            if (!inSection || equals < 0 || !line[..equals].Trim().Equals(EnabledKey, StringComparison.OrdinalIgnoreCase)) continue;

            // The last assignment wins, as it does when the engine reads the file.
            enabled = line[(equals + 1)..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return enabled;
    }
}
