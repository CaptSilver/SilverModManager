using SilverModManager.Sdk.Errors;

namespace SilverModManager.Games.Larian.Engine;

/// <summary>
/// The game has not written its mod list yet, and this game's list cannot be made from nothing: the file says
/// which adventure the player uses, and only the game knows.
/// </summary>
public sealed class LarianModListMissingException(string gameName)
    : UserFacingException($"{gameName} has not written its mod list yet. Start the game once, then apply again.")
{
    /// <inheritdoc />
    public override string Title => "The game has no mod list yet";
}
