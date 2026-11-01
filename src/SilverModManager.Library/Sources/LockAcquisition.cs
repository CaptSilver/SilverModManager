using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.Sources;

/// <summary>
/// A file a lock import fetches again. It is for the game of the loadout the lock is imported into, or for none when
/// the import has no game.
/// </summary>
[PublicAPI]
public sealed record LockAcquisition(LibrarySourceContext Context) : IAcquisitionContext
{
    /// <inheritdoc />
    public Destination Destination(IDb db)
        => Context.Game is { } game ? Sdk.Library.Destination.Of(game.GameId) : Sdk.Library.Destination.Unknown;
}
