using JetBrains.Annotations;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;

/// <summary>
/// Thrown when a loadout's copy of a game INI is no longer in the file store. Writing the INI from nothing would
/// replace the player's settings with an empty file, so the apply stops instead.
/// </summary>
[PublicAPI]
public class IniBaselineMissingException(GamePath path)
    : UserFacingException(
        $"This loadout's copy of {path.FileName} is missing from the app's file store, so the file was not written. " +
        "Turn off \"Own the game INIs\" for the loadout to leave the file to the game."
    )
{
    /// <inheritdoc />
    public override string Title => "A game INI was not written";
}
