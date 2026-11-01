using JetBrains.Annotations;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Sdk.Library;

/// <summary>
/// A game cannot be taken off a library item while the item is installed in a loadout of that game. Uninstalling
/// it first is the player's decision, not a side effect of editing the item's games.
/// </summary>
[PublicAPI]
public sealed class DestinationStillInstalledException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The game was not removed";
}
