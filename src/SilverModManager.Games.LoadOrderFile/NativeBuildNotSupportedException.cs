using SilverModManager.Sdk.Errors;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>The install is a game's native Linux build, whose layout this app does not deploy to.</summary>
public sealed class NativeBuildNotSupportedException(string game)
    : UserFacingException($"{MarkdownText.Inline(game)} is installed as its Linux build, which keeps its mods and options in a different layout from the Windows build this app manages, so nothing deployed here would take effect. In Steam, open the game's Properties, choose Compatibility, force Proton, let Steam install the Windows files, and add the game again.")
{
    public override string Title => "This is the Linux build";
}
