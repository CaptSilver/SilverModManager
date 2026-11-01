using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Library;

/// <summary>
/// A modlist file is for the game the list is built for, and so is a file the app fetched for a list. A file fetched
/// for lists of several games is for each of them.
/// </summary>
/// <remarks>
/// It does not warn about a list for a game the app has no title for: <see cref="ModlistAcquisition"/> says so once
/// per list, and a warning here would also use up that list's one warning.
/// </remarks>
public sealed class ModlistFetchInference : IDestinationInference
{
    /// <inheritdoc />
    public int Priority => 20;

    /// <inheritdoc />
    public Destination Infer(IDb db, LibraryItem.ReadOnly item)
    {
        var games = new HashSet<GameId>();

        var list = WabbajackModlistLibraryFile.Load(db, item.Id);
        if (list.IsValid()) games.UnionWith(ModlistAcquisition.ForList(item.Id, list.GameType, logger: null).Games);

        foreach (var datom in db.Datoms(WabbajackModlistArchive.FetchedFiles, item.Id))
        {
            var fetchedFor = WabbajackModlistArchive.Load(db, datom.E).Modlist;
            games.UnionWith(ModlistAcquisition.ForList(fetchedFor.Id, fetchedFor.GameType, logger: null).Games);
        }

        return games.Count == 0 ? Destination.Unknown : new Destination(games, Shared: false);
    }
}
