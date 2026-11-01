using System.Reactive.Linq;
using DynamicData;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;

namespace SilverModManager.App.UI.Pages.LibraryPage;

/// <summary>
/// What a Library row has to say about the game it is listed under: whether Nexus Mods could not tell its game from a
/// sibling's, whether it has no game at all, which other games it is for, and whether a loadout of this game has it
/// installed. A row for several items (a mod page) combines the state of each.
/// </summary>
/// <param name="OtherGames">The games the row's items are for besides the one the page is showing.</param>
/// <param name="InstalledHere">Some item is linked into a loadout of the page's game, so the game cannot be taken off it.</param>
public sealed record DestinationRowState(bool IsShared, bool IsUnassigned, IReadOnlySet<GameId> OtherGames, bool InstalledHere)
{
    public static readonly DestinationRowState None = new(false, false, new HashSet<GameId>(), false);

    // A record compares IReadOnlySet by reference; two states naming the same games must be equal, or every change
    // elsewhere on the item would re-emit.
    public bool Equals(DestinationRowState? other)
        => other is not null
           && IsShared == other.IsShared && IsUnassigned == other.IsUnassigned && InstalledHere == other.InstalledHere
           && OtherGames.SetEquals(other.OtherGames);

    public override int GetHashCode() => HashCode.Combine(IsShared, IsUnassigned, InstalledHere, OtherGames.Count);

    public static DestinationRowState Of(LibraryItem.ReadOnly item, GameId game, bool installedHere)
        => new(
            IsShared: item.Contains(LibraryItem.DestinationsShared),
            IsUnassigned: item.Contains(LibraryItem.DestinationUnknown),
            OtherGames: LibraryDestinations.GamesOf(item).Where(other => other != game).ToHashSet(),
            InstalledHere: installedHere);

    public static DestinationRowState Combine(IEnumerable<DestinationRowState> states)
    {
        var all = states.ToArray();
        return all.Length == 0
            ? None
            : new DestinationRowState(
                all.Any(state => state.IsShared),
                all.Any(state => state.IsUnassigned),
                all.SelectMany(state => state.OtherGames).ToHashSet(),
                all.Any(state => state.InstalledHere));
    }

    /// <summary>The state now, for the value a row starts with before its first change arrives.</summary>
    public static DestinationRowState Read(IConnection connection, IEnumerable<LibraryItemId> ids, GameId game)
    {
        var db = connection.Db;
        return Combine(ids.Select(id => Of(LibraryItem.Load(db, id), game, LibraryDataProviderHelper.IsInstalledForGame(db, id, game))));
    }

    /// <summary>The state as the items' games, markers and loadout links change.</summary>
    public static IObservable<DestinationRowState> Observe(IConnection connection, IObservable<IChangeSet<LibraryItemId, EntityId>> ids, GameId game)
        => ids
            .TransformOnObservable(id => LibraryItem.Observe(connection, id)
                .CombineLatest(LibraryDataProviderHelper.ObserveInstalledForGame(connection, id, game), (item, installed) => Of(item, game, installed)))
            .QueryWhenChanged(query => Combine(query.Items))
            .DistinctUntilChanged();
}
