using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Sdk.Library;

/// <summary>An item acquired for one named game.</summary>
[PublicAPI]
public sealed record GameAcquisition(GameId Game) : IAcquisitionContext
{
    /// <inheritdoc />
    public Destination Destination(IDb db) => Library.Destination.Of(Game);
}
