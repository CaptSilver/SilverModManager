using System.Collections.Immutable;
using JetBrains.Annotations;
using NexusMods.Sdk.IO;

namespace NexusMods.Sdk.Games;

/// <summary>
/// Supplies cover art for a game from what the user's own machine already holds, so the
/// app ships no publisher artwork. Every call returns an image; when nothing is found the
/// image is a neutral placeholder.
/// </summary>
[PublicAPI]
public interface IGameArtSource
{
    /// <summary>
    /// The portrait cover, used for the game tile. The first id that has art wins.
    /// </summary>
    IStreamFactory Tile(ImmutableArray<uint> steamAppIds);

    /// <summary>
    /// The small square icon. Falls back to the cover when only that is available.
    /// </summary>
    IStreamFactory Icon(ImmutableArray<uint> steamAppIds);
}
