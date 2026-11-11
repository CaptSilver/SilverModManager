using SilverModManager.Sdk.Errors;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tools;

/// <summary>
/// The shell command that installs a winetricks verb into a prefix, for a component the app only detects.
/// </summary>
public static class PrefixInstallCommand
{
    /// <summary>
    /// A Steam prefix goes through protontricks, which finds the game's Proton build; any other prefix is named
    /// directly to winetricks.
    /// </summary>
    public static string For(ResolvedPrefix prefix, string verb)
        => prefix.Kind == CompatibilityLayerKind.SteamProton && prefix.ProtonAppId is { } appId
            ? $"protontricks {appId} -q {verb}"
            : $"WINEPREFIX={ShellText.Quote(prefix.Path.ToString())} winetricks -q {verb}";
}
