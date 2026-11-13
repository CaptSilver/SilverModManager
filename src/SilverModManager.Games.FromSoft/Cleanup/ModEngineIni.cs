namespace SilverModManager.Games.FromSoft.Cleanup;

/// <summary>Reads the one setting of Mod Engine 1's <c>modengine.ini</c> that matters to a cleanup.</summary>
public static class ModEngineIni
{
    /// <summary>What Mod Engine 1 loads from when the ini names no folder.</summary>
    public const string DefaultOverrideDirectory = "doa";

    /// <summary>
    /// The folder Mod Engine 1 loads overrides from: <c>modOverrideDirectory</c> of the <c>[files]</c> section, without
    /// quotes or the separators around it. The ini is a Windows file, so the value is usually <c>\mods</c>.
    /// </summary>
    public static string OverrideDirectory(string iniText)
    {
        var inFiles = false;
        foreach (var raw in iniText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inFiles = line.Equals("[files]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inFiles || line.StartsWith(';') || line.StartsWith('#')) continue;

            var equals = line.IndexOf('=');
            if (equals < 0 || !line[..equals].Trim().Equals("modOverrideDirectory", StringComparison.OrdinalIgnoreCase)) continue;

            var value = line[(equals + 1)..].Trim().Trim('"', '\'').Replace('\\', '/').Trim('/').Trim();
            return value.Length == 0 ? DefaultOverrideDirectory : value;
        }

        return DefaultOverrideDirectory;
    }
}
