using System.Reactive.Linq;
using DynamicData;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.App.UI.Pages;

/// <summary>
/// Finds the Wabbajack modlists in the library for game removal. They have no Mods tab row: they are cards on the
/// Collections tab, and <see cref="LocalFileDataProvider"/> leaves them out too.
/// </summary>
[UsedImplicitly]
internal class WabbajackModlistDataProvider : ILibraryDataProvider
{
    private readonly IConnection _connection;

    public WabbajackModlistDataProvider(IServiceProvider serviceProvider)
    {
        _connection = serviceProvider.GetRequiredService<IConnection>();
    }

    // Removing a game deletes these files, so only the lists made for that game count; a list whose game the app has
    // no title for belongs to no game and must not go with whichever one is removed.
    public LibraryFile.ReadOnly[] GetAllFiles(GameId gameId, IDb? db = null)
    {
        db ??= _connection.Db;
        return WabbajackModlistLibraryFile
            .All(db)
            .Where(file => WabbajackGames.GameIdFor(file.GameType) == gameId)
            .Select(static file => file.AsLibraryFile())
            .ToArray();
    }

    // Modlists are on the Collections tab, so the Mods tab has no row for them. The provider stays registered because
    // removing a game still asks it for the lists to delete.
    public IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> ObserveLibraryItems(LibraryFilter libraryFilter)
        => Observable.Never<IChangeSet<CompositeItemModel<EntityId>, EntityId>>().StartWith(ChangeSet<CompositeItemModel<EntityId>, EntityId>.Empty);

    public IObservable<int> CountLibraryItems(LibraryFilter libraryFilter) => Observable.Never<int>().StartWith(0);
}
