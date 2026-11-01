namespace SilverModManager.Abstractions.Games.Tools;

/// <summary>The placeholders an argument template can use, written <c>{Name}</c>.</summary>
public static class ToolVariableNames
{
    /// <summary>The installation's game folder. Always available.</summary>
    public const string GameFolder = "GameFolder";

    /// <summary>The folder mods are installed into. Supplied by the game.</summary>
    public const string DataFolder = "DataFolder";

    /// <summary>The game's plugin list file. Supplied by the game.</summary>
    public const string PluginsFile = "PluginsFile";

    /// <summary>The game's main ini file (the file, not its folder). Supplied by the game.</summary>
    public const string IniFile = "IniFile";

    /// <summary>The loadout's name. Always available.</summary>
    public const string LoadoutName = "LoadoutName";

    /// <summary>The run's output folder. Bound by the caller that creates it.</summary>
    public const string OutputFolder = "OutputFolder";

    /// <summary>The tool's own path. Bound by the caller.</summary>
    public const string Exe = "Exe";
}
