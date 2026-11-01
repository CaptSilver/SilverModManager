using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

namespace SilverModManager.App.UI.Pages.LibraryPage;

/// <summary>What a row's destination action asks for.</summary>
public enum DestinationActionKind
{
    UseForThisGame,
    SuggestAGame,
    OnlyForThisGame,
    RemoveFromThisGame,
}

/// <summary>
/// The row actions that settle which game a Library item is for, for the game the page shows. Each one goes through
/// <see cref="ILibraryService"/>; nothing here writes a datom. The refusal to take a game off an item installed in one
/// of its loadouts comes from the service as a <see cref="UserFacingException"/>, so the caller shows it.
/// </summary>
public sealed class LibraryDestinationActions(ILibraryService libraryService, IConnection connection, GameId game)
{
    /// <summary>Adds the page's game to the items and clears their "no game" marker.</summary>
    public Task UseForThisGame(IEnumerable<LibraryItemId> ids) => libraryService.AddDestination(Load(ids), game);

    /// <summary>Drops every other game from the items and clears their shared marker.</summary>
    public Task OnlyForThisGame(IEnumerable<LibraryItemId> ids) => libraryService.KeepOnlyDestination(Load(ids), game);

    /// <summary>Takes the page's game off the items. One left with no game is marked unknown, not deleted.</summary>
    public Task RemoveFromThisGame(IEnumerable<LibraryItemId> ids) => libraryService.RemoveDestination(Load(ids), game);

    /// <summary>What the inferences say the items are for. Changes nothing until <see cref="Accept"/>.</summary>
    public Destination Suggest(IEnumerable<LibraryItemId> ids) => libraryService.SuggestDestination(Load(ids));

    /// <summary>Applies a suggestion the user accepted.</summary>
    public Task Accept(IEnumerable<LibraryItemId> ids, Destination suggestion) => libraryService.AddDestination(Load(ids), suggestion);

    private LibraryItem.ReadOnly[] Load(IEnumerable<LibraryItemId> ids)
    {
        var db = connection.Db;
        return ids.Select(id => LibraryItem.Load(db, id)).Where(item => item.IsValid()).ToArray();
    }
}
