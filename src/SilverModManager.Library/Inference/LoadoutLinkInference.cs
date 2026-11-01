using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.Inference;

/// <summary>
/// An item installed into a loadout is for that loadout's game. Installed into loadouts of several games, it is for
/// each of them; nothing is shared, because the user put it in each one. This is also what establishes the rule that an
/// item linked into a loadout of a game has that game, for items that were linked before the rule was kept.
/// </summary>
public sealed class LoadoutLinkInference : IDestinationInference
{
    /// <inheritdoc />
    public int Priority => 10;

    /// <inheritdoc />
    public Destination Infer(IDb db, LibraryItem.ReadOnly item)
    {
        var games = new HashSet<GameId>();
        foreach (var link in LibraryLinkedLoadoutItem.FindByLibraryItem(db, item.Id))
            games.UnionWith(new LoadoutAcquisition(link.AsLoadoutItem().LoadoutId).Destination(db).Games);

        return games.Count == 0 ? Destination.Unknown : new Destination(games, Shared: false);
    }
}
