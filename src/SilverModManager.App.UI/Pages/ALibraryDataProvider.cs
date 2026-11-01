using DynamicData;
using DynamicData.Kernel;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Library;
using SilverModManager.App.UI.Controls;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages;

/// <summary>
/// The shared half of a library provider: every per-game query starts from the slice of items destined for the game,
/// so a provider only says which model it owns and how its rows look.
/// </summary>
/// <typeparam name="TModel">The generated <c>ReadOnly</c> struct of the model the provider lists.</typeparam>
public abstract class ALibraryDataProvider<TModel>(IConnection connection) : ILibraryDataProvider
    where TModel : struct, IReadOnlyModel<TModel>
{
    /// <summary>The game's items plus the unassigned ones, narrowed to this provider's model.</summary>
    protected IObservable<IChangeSet<TModel, EntityId>> ObserveOwnItems(LibraryFilter filter)
        => Narrow(LibraryDestinations.ObserveShownFor(connection, filter.Game.GameId));

    /// <summary>
    /// The loadout's game's items, for the loadout page. Unassigned items are left out: an item linked into the
    /// loadout already has the loadout's game.
    /// </summary>
    protected IObservable<IChangeSet<TModel, EntityId>> ObserveOwnItemsFor(GameId loadoutGame)
        => Narrow(LibraryDestinations.ObserveItemsFor(connection, loadoutGame));

    /// <summary>
    /// The items of the game the loadout is for. A loadout whose installation records no game has none: nothing
    /// is destined for it.
    /// </summary>
    protected IObservable<IChangeSet<TModel, EntityId>> ObserveOwnItemsForLoadout(LoadoutId loadout)
    {
        var games = new LoadoutAcquisition(loadout).Destination(connection.Db).Games;
        return games.Count == 0
            ? System.Reactive.Linq.Observable.Return(ChangeSet<TModel, EntityId>.Empty)
            : ObserveOwnItemsFor(games.First());
    }

    private IObservable<IChangeSet<TModel, EntityId>> Narrow(IObservable<IChangeSet<Datom, EntityId>> slice)
        => slice
            .Transform(datom => TModel.Create(connection.Db, datom.E))
            .FilterImmutable(static model => model.IsValid());

    /// <inheritdoc />
    public LibraryFile.ReadOnly[] GetAllFiles(GameId game, IDb? db = null)
    {
        db ??= connection.Db;
        return LibraryDestinations.ItemsFor(db, game)
            .Select(item => TModel.Create(db, item.Id))
            .Where(static model => model.IsValid())
            .Select(AsLibraryFile)
            .Where(static file => file.HasValue)
            .Select(static file => file.Value)
            .ToArray();
    }

    /// <summary>
    /// The model's file, or none for a row that has no file. The Nexus provider skips those, as it always has.
    /// </summary>
    protected abstract Optional<LibraryFile.ReadOnly> AsLibraryFile(TModel model);

    /// <inheritdoc />
    public abstract IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> ObserveLibraryItems(LibraryFilter filter);

    /// <inheritdoc />
    public abstract IObservable<int> CountLibraryItems(LibraryFilter filter);
}
