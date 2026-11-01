using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

namespace SilverModManager.App.UI.Pages.MyGames;

/// <summary>
/// What removing a game does to the library files it was for. Files only this game uses are deleted; files another
/// game still uses stay, with this game taken off them. The remove dialog previews from the same split.
/// </summary>
internal sealed class GameFileRemoval
{
    private readonly GameId _game;

    private GameFileRemoval(GameId game, LibraryFile.ReadOnly[] filesToDelete, LibraryItem.ReadOnly[] sharedItems)
    {
        _game = game;
        FilesToDelete = filesToDelete;
        SharedItems = sharedItems;
    }

    /// <summary>Files that only <see cref="_game"/> uses.</summary>
    public LibraryFile.ReadOnly[] FilesToDelete { get; }

    /// <summary>Items another game still uses.</summary>
    public LibraryItem.ReadOnly[] SharedItems { get; }

    public Size TotalSize => FilesToDelete.Aggregate(Size.Zero, static (total, file) => total + file.Size);

    public static GameFileRemoval Plan(IDb db, IEnumerable<LibraryFile.ReadOnly> files, GameId game)
    {
        var byItem = files.DistinctBy(static file => file.Id).ToDictionary(static file => file.Id);
        var (onlyThisGame, shared) = LibraryDestinations.Partition(db, byItem.Values.Select(static file => file.AsLibraryItem()), game);
        return new GameFileRemoval(game, onlyThisGame.Select(item => byItem[item.Id]).ToArray(), shared.ToArray());
    }

    /// <summary>
    /// Deletes the game's own files and takes the game off the shared ones. Run after the game's loadouts are gone,
    /// so no item is still installed for the game and taking it off cannot be refused.
    /// </summary>
    public async Task Apply(ILibraryService library)
    {
        await library.RemoveLibraryItems(FilesToDelete.Select(static file => file.AsLibraryItem()));
        await library.RemoveDestination(SharedItems, _game);
    }
}
