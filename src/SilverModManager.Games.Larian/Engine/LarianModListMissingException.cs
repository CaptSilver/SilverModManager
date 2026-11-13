using SilverModManager.Abstractions.Loadouts.Synchronizers;

namespace SilverModManager.Games.Larian.Engine;

/// <summary>
/// The game has not written its mod list yet, and this game's list cannot be made from nothing: the file says
/// which adventure the player uses, and only the game knows.
/// </summary>
/// <remarks>A preflight refusal, so managing the game before it has run leaves no half-added loadout behind.</remarks>
public sealed class LarianModListMissingException(string gameName)
    : SynchronizerPreflightException($"{gameName} has not written its mod list yet. Start the game once, then apply again.")
{
    /// <inheritdoc />
    public override string Title => "The game has no mod list yet";
}
