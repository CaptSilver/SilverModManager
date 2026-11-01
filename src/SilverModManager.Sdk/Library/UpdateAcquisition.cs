using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Sdk.Library;

/// <summary>
/// A new file that replaces older items. It is for what they were for, whichever page started the update, so an
/// update of a VR-only file stays VR-only.
/// </summary>
[PublicAPI]
public sealed class UpdateAcquisition(IEnumerable<LibraryItemId> replaced) : IAcquisitionContext
{
    private readonly LibraryItemId[] _replaced = replaced.ToArray();

    /// <inheritdoc />
    public Destination Destination(IDb db)
    {
        var games = new HashSet<Games.GameId>();
        var shared = false;
        foreach (var id in _replaced)
        {
            var item = LibraryItem.Load(db, id);
            games.UnionWith(LibraryDestinations.GamesOf(item));
            shared |= item.Contains(LibraryItem.DestinationsShared);
        }

        return new Destination(games, shared && games.Count > 0);
    }
}
