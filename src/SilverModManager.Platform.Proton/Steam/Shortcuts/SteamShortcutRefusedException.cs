using SilverModManager.Sdk.Errors;

namespace SilverModManager.Platform.Proton.Steam.Shortcuts;

/// <summary>
/// The shortcut was not added or removed, and the message says why in words the player can act on.
/// </summary>
public sealed class SteamShortcutRefusedException(string message, bool removing) : UserFacingException(message)
{
    /// <summary>
    /// The refusal is that Steam is running, so closing it and trying again is the way past it.
    /// </summary>
    public bool SteamRunning { get; init; }

    public override string Title => removing ? "Not removed from Steam" : "Not added to Steam";

    public override string? CommandLineHint => "silvermodmanager steam-shortcut add|remove|status [--account <id>]";
}
