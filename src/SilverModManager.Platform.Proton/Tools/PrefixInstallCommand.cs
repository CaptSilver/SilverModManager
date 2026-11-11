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

    /// <summary>
    /// The Markdown sentence that tells the user to run <see cref="For"/>. A prefix folder whose name has a line break
    /// or other control character cannot be shown in a code span without changing it, so the sentence says that
    /// instead of showing a command that names a different folder.
    /// </summary>
    public static string InstallSentence(ResolvedPrefix prefix, string verb)
    {
        var command = For(prefix, verb);
        return MarkdownText.ShowsExactly(command)
            ? $"Install it with {MarkdownText.Code(command)}."
            : MarkdownText.NoCommandFor("prefix folder");
    }
}
