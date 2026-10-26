using System.Globalization;
using SilverModManager.App.UI.Resources;
using SilverModManager.Sdk.Ini;

namespace SilverModManager.App.UI.Pages.Ini;

public enum IniSettingType
{
    /// <summary>0 or 1.</summary>
    Bool,
    Int,
    Float,
    /// <summary>Anything that fits on one line.</summary>
    Text,
}

/// <summary>Which of a game's INI files honours a setting.</summary>
public enum IniFileRole
{
    /// <summary>The game's main INI, the first file that is neither a preferences nor a custom file.</summary>
    Main,

    /// <summary>The preferences INI the game's launcher and options menu write; the main INI when the game has no such file.</summary>
    Prefs,
}

/// <param name="Label">What the setting is called on the page.</param>
/// <param name="Section">The INI section the key sits in.</param>
/// <param name="Key">The key as the game reads it.</param>
/// <param name="Type">What a value must look like.</param>
/// <param name="File">Which file the game honours the key in.</param>
public sealed record IniSettingDefinition(string Label, string Section, string Key, IniSettingType Type, IniFileRole File)
{
    /// <summary>What a value of this setting is described as in a refusal.</summary>
    public string TypeDescription => Type switch
    {
        IniSettingType.Bool => Language.IniPage_Type_Bool,
        IniSettingType.Int => Language.IniPage_Type_Int,
        IniSettingType.Float => Language.IniPage_Type_Float,
        _ => Language.IniPage_Type_Text,
    };

    /// <summary>Whether <paramref name="value"/> is acceptable for <see cref="Type"/>. An empty value is never one.</summary>
    public bool Accepts(string value) => Type switch
    {
        IniSettingType.Bool => value is "0" or "1",
        IniSettingType.Int => int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
        IniSettingType.Float => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
        _ => value.Length > 0 && !value.Contains('\n') && !value.Contains('\r'),
    };
}

/// <summary>
/// The settings the INI page offers as typed fields. A short list of the keys players change most, written for this
/// app: a key is offered on the tab of the file that honours it, and anything else is edited as raw text.
/// </summary>
public static class IniSettingsCatalog
{
    public static IReadOnlyList<IniSettingDefinition> All { get; } =
    [
        new(Language.IniPage_Setting_SavePath, "General", "sLocalSavePath", IniSettingType.Text, IniFileRole.Main),
        new(Language.IniPage_Setting_ArchiveInvalidation, "Archive", "bInvalidateOlderFiles", IniSettingType.Bool, IniFileRole.Main),
        new(Language.IniPage_Setting_TestFile + " 1", "General", "sTestFile1", IniSettingType.Text, IniFileRole.Main),
        new(Language.IniPage_Setting_Fullscreen, "Display", "bFull Screen", IniSettingType.Bool, IniFileRole.Prefs),
        new(Language.IniPage_Setting_Borderless, "Display", "bBorderless", IniSettingType.Bool, IniFileRole.Prefs),
        new(Language.IniPage_Setting_ResolutionWidth, "Display", "iSize W", IniSettingType.Int, IniFileRole.Prefs),
        new(Language.IniPage_Setting_ResolutionHeight, "Display", "iSize H", IniSettingType.Int, IniFileRole.Prefs),
        new(Language.IniPage_Setting_Vsync, "Display", "iPresentInterval", IniSettingType.Int, IniFileRole.Prefs),
        new(Language.IniPage_Setting_Fov, "Display", "fDefaultWorldFOV", IniSettingType.Float, IniFileRole.Prefs),
    ];

    /// <summary>The file of <paramref name="files"/> that honours settings of <paramref name="role"/>, or null when the game has none.</summary>
    public static string? FileFor(IniSet files, IniFileRole role)
    {
        var names = files.FileNames;
        var main = names.FirstOrDefault(n => !IsPrefs(n) && !IsCustom(n));
        return role == IniFileRole.Main ? main : names.LastOrDefault(IsPrefs) ?? main;
    }

    private static bool IsPrefs(string name) => name.Contains("Prefs", StringComparison.OrdinalIgnoreCase);

    private static bool IsCustom(string name) => name.Contains("Custom", StringComparison.OrdinalIgnoreCase);
}
