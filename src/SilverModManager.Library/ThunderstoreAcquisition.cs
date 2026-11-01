using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library;

/// <summary>
/// A Thunderstore package added for a game. Thunderstore support is a mix-in on a game's data, so the game's id comes
/// from that same object; a stand-in that is not game data names no game.
/// </summary>
[PublicAPI]
public sealed record ThunderstoreAcquisition(IThunderstoreGame Game) : IAcquisitionContext
{
    /// <inheritdoc />
    public Destination Destination(IDb db)
        => Game is IGameData data ? Sdk.Library.Destination.Of(data.GameId) : Sdk.Library.Destination.Unknown;
}
