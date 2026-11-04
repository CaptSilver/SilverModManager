using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Backend.Games;

/// <summary>
/// One link of the <see cref="GameArtChain"/>: a place art can come from. Returns null when this link has
/// nothing for the game, which sends the chain on to the next one. A link never throws for "not found" and
/// never touches disk or network until asked.
/// </summary>
internal interface IGameArtProvider
{
    ValueTask<IStreamFactory?> FindTileAsync(GameArtRequest request);

    ValueTask<IStreamFactory?> FindIconAsync(GameArtRequest request);
}
