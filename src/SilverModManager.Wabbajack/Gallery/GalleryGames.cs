using SilverModManager.Sdk.Games;

namespace SilverModManager.Wabbajack.Gallery;

/// <summary>
/// Maps the game ids the gallery uses (the lower-case names of Wabbajack's game list) to this app's games. The
/// Bethesda family is the table installs already use in <see cref="WabbajackGames"/>; the gallery also lists
/// games Wabbajack can't install here yet, and those only get a title to be browsed under.
/// </summary>
public static class GalleryGames
{
    private static readonly Dictionary<string, GameId> BrowseOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cyberpunk2077"] = GameId.From("RedEngine.Cyberpunk2077"),
        ["baldursgate3"] = GameId.From("Larian.BaldursGate3"),
    };

    /// <summary>The game of this app a gallery list is for, or null for a game it has no title for.</summary>
    public static GameId? GameIdFor(string galleryGame)
        => WabbajackGames.GameIdFor(galleryGame) ?? (BrowseOnly.TryGetValue(galleryGame, out var id) ? id : null);
}
