using JetBrains.Annotations;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// Supplies cover art for a game without the app shipping any publisher artwork: from Steam's own cache on
/// this machine, then from Steam's public image servers, then drawn from the game's name. Every call returns
/// an image.
/// </summary>
[PublicAPI]
public interface IGameArtSource
{
    /// <summary>
    /// The portrait cover, used for the game tile. The first Steam id that has art wins.
    /// </summary>
    IStreamFactory Tile(GameArtRequest request);

    /// <summary>
    /// The small square icon. Falls back to the cover when only that is available.
    /// </summary>
    IStreamFactory Icon(GameArtRequest request);
}
