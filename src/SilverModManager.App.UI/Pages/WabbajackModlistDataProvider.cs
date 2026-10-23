using System.Reactive.Linq;
using DynamicData;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.App.UI.Controls;
using NexusMods.App.UI.Pages.LibraryPage;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Wabbajack;
using NexusMods.Wabbajack.Models;

namespace NexusMods.App.UI.Pages;

/// <summary>
/// Lists the Wabbajack modlists in the library, one row each, named after the list. A modlist is also a local file
/// (the user added it from disk), so <see cref="LocalFileDataProvider"/> leaves these out. The row opens the
/// modlist's page instead of installing the file as a mod.
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

    // A list for a game this app has no title for stays visible in every library: hiding it would make the file
    // impossible to find and remove.
    private static bool IsForGame(WabbajackModlistLibraryFile.ReadOnly file, GameId gameId)
        => WabbajackGames.GameIdFor(file.GameType) is not { } listGame || listGame == gameId;

    public IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> ObserveLibraryItems(LibraryFilter libraryFilter)
    {
        return WabbajackModlistLibraryFile
            .ObserveAll(_connection)
            .FilterImmutable(file => IsForGame(file, libraryFilter.Game.GameId))
            .Transform(ToLibraryItemModel);
    }

    public IObservable<int> CountLibraryItems(LibraryFilter libraryFilter)
    {
        return WabbajackModlistLibraryFile
            .ObserveAll(_connection)
            .FilterImmutable(file => IsForGame(file, libraryFilter.Game.GameId))
            .QueryWhenChanged(static query => query.Count)
            .Prepend(0);
    }

    private CompositeItemModel<EntityId> ToLibraryItemModel(WabbajackModlistLibraryFile.ReadOnly file)
    {
        var libraryFile = file.AsLibraryFile();
        var itemModel = new CompositeItemModel<EntityId>(file.Id);

        itemModel.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: file.Name));
        itemModel.Add(SharedColumns.Name.ImageComponentKey, new ImageComponent(value: ImagePipelines.ModPageThumbnailFallback));
        itemModel.Add(LibraryColumns.DownloadedDate.ComponentKey, new DateComponent(value: file.GetCreatedAt()));
        itemModel.Add(LibraryColumns.ItemVersion.CurrentVersionComponentKey, new VersionComponent(value: file.Version));
        itemModel.Add(SharedColumns.ItemSize.ComponentKey, new SizeComponent(value: libraryFile.Size));
        itemModel.Add(LibraryColumns.Actions.LibraryItemIdsComponentKey, new LibraryComponents.LibraryItemIds(libraryFile.AsLibraryItem()));

        // No install button: adding the zip as a mod would be wrong. The row opens the modlist's page.
        itemModel.Add(LibraryColumns.Actions.OpenModlistComponentKey, new LibraryComponents.OpenModlistAction());
        LibraryDataProviderHelper.AddViewChangelogActionComponent(itemModel, isEnabled: false);
        LibraryDataProviderHelper.AddViewModPageActionComponent(itemModel, isEnabled: false);
        LibraryDataProviderHelper.AddHideUpdatesActionComponent(itemModel, isEnabled: false, isVisible: false);
        LibraryDataProviderHelper.AddDeleteItemActionComponent(itemModel);

        return itemModel;
    }
}
