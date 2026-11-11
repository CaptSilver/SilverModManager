using JetBrains.Annotations;

namespace SilverModManager.Sdk.Errors;

/// <summary>
/// Puts values the app did not write (paths, loadout names, launch-option lines) into a shell command it shows or
/// writes for the user to run. <see cref="MarkdownText"/> is the counterpart for Markdown.
/// </summary>
[PublicAPI]
public static class ShellText
{
    // Steam replaces a literal %command% in a launch-option line textually, before any shell sees the line.
    private const string SteamCommandToken = "%command%";

    /// <summary>
    /// POSIX single-quotes a value for a shell command line. An embedded quote becomes <c>'\''</c>. A literal
    /// <c>%command%</c> is split into <c>%comm''and%</c> so Steam's substitution in a launch-option line cannot hit
    /// it; the shell joins the pieces back, so the value is the same in any shell command.
    /// </summary>
    public static string Quote(string value)
        => "'" + value.Replace("'", @"'\''").Replace(SteamCommandToken, "%comm''and%") + "'";
}
