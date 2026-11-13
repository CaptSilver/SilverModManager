using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Games.Larian.Engine;

/// <summary>
/// The game has more than one player profile and the player has not said which one the app writes the mod list of.
/// </summary>
/// <remarks>A preflight refusal, so managing the game before a profile is picked leaves no half-added loadout behind.</remarks>
public sealed class LarianProfileUnresolvedException(string gameName)
    : SynchronizerPreflightException($"{MarkdownText.Inline(gameName)} has more than one player profile. Pick one under Settings, then apply again.")
{
    /// <inheritdoc />
    public override string Title => "Pick a player profile";
}
