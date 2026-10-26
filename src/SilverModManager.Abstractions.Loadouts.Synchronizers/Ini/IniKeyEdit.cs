namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;

/// <summary>
/// A key the app owns in a game INI, set to <paramref name="Value"/> on every bake whatever the baseline or a
/// tweak says.
/// </summary>
public readonly record struct IniKeyEdit(string Section, string Key, string Value);
