using DynamicData;
using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Sdk.Library;

/// <summary>
/// The one place that reads and writes <see cref="LibraryItem.DestinationGames"/>,
/// <see cref="LibraryItem.DestinationsShared"/> and <see cref="LibraryItem.DestinationUnknown"/>, so no caller
/// builds a slice or writes a datom for them by hand.
/// </summary>
[PublicAPI]
public static class LibraryDestinations
{
    /// <summary>The items destined for <paramref name="game"/>, one datom per item.</summary>
    public static IObservable<IChangeSet<Datom, EntityId>> ObserveItemsFor(IConnection conn, GameId game)
        => conn.ObserveDatoms(SliceDescriptor.Create(LibraryItem.DestinationGames, game, conn.AttributeCache)).AsEntityIds();

    /// <summary>The items no source, loadout or modlist has assigned a game.</summary>
    public static IObservable<IChangeSet<Datom, EntityId>> ObserveUnassigned(IConnection conn)
        => conn.ObserveDatoms(SliceDescriptor.Create(LibraryItem.DestinationUnknown, Null.Instance, conn.AttributeCache)).AsEntityIds();

    /// <summary>
    /// What a game's Library page lists: the game's slice plus the unassigned slice. Joined with the DynamicData
    /// union by key, never <c>Merge</c>: moving an item from unassigned to the game arrives as two changesets, and
    /// a Remove from one after the Add from the other must not drop a row that should stay.
    /// </summary>
    public static IObservable<IChangeSet<Datom, EntityId>> ObserveShownFor(IConnection conn, GameId game)
        => ObserveItemsFor(conn, game).Or(ObserveUnassigned(conn));

    /// <summary>The items destined for <paramref name="game"/> in a snapshot.</summary>
    public static IEnumerable<LibraryItem.ReadOnly> ItemsFor(IDb db, GameId game)
        => db.Datoms(LibraryItem.DestinationGames, game).Select(datom => LibraryItem.Load(db, datom.E));

    /// <summary>The items with no game assigned in a snapshot.</summary>
    public static IEnumerable<LibraryItem.ReadOnly> Unassigned(IDb db)
        => db.Datoms(LibraryItem.DestinationUnknown, Null.Instance).Select(datom => LibraryItem.Load(db, datom.E));

    /// <summary>The games the item is for.</summary>
    public static IReadOnlySet<GameId> GamesOf(LibraryItem.ReadOnly item)
        => item.DestinationGames.ToHashSet();

    /// <summary>Splits a game's items into those for that game only and those another game still uses.</summary>
    public static (IReadOnlyList<LibraryItem.ReadOnly> OnlyThisGame, IReadOnlyList<LibraryItem.ReadOnly> Shared) Partition(
        IDb db, IEnumerable<LibraryItem.ReadOnly> items, GameId game)
    {
        var onlyThisGame = new List<LibraryItem.ReadOnly>();
        var shared = new List<LibraryItem.ReadOnly>();
        foreach (var item in items)
        {
            var current = LibraryItem.Load(db, item.Id);
            var usedElsewhere = current.DestinationGames.Any(other => other != game);
            (usedElsewhere ? shared : onlyThisGame).Add(current);
        }
        return (onlyThisGame, shared);
    }

    /// <summary>
    /// Unions the games into the item's set and clears DestinationUnknown. Sets DestinationsShared only when the
    /// destination is shared and the item had no games before; never clears it ("Only for this game" does).
    /// Queued as a tx function.
    /// </summary>
    public static void Add(ITransaction tx, EntityId item, Destination destination)
        => tx.Add(new AddDestinationTxFunc(item, destination));

    /// <summary>Removes a game; when the set empties, sets DestinationUnknown. Queued as a tx function.</summary>
    public static void Remove(ITransaction tx, EntityId item, GameId game)
        => tx.Add(new RemoveDestinationTxFunc(item, game));

    /// <summary>
    /// Writes the first destination of an item created in this transaction: its games, Shared when it says so,
    /// Unknown when it has none. Asserts only, so it is called exactly once per new item.
    /// </summary>
    public static void Write(ITransaction tx, EntityId item, Destination destination)
    {
        foreach (var game in destination.Games)
            tx.Add(item, LibraryItem.DestinationGames, game);

        if (destination.IsUnknown)
            tx.Add(item, LibraryItem.DestinationUnknown, Null.Instance);
        else if (destination.Shared)
            tx.Add(item, LibraryItem.DestinationsShared, Null.Instance);
    }

    /// <summary>Reads the set from the commit's own basis, so two racing edits cannot both see the old set.</summary>
    private sealed class AddDestinationTxFunc(EntityId item, Destination destination) : ITxFunction
    {
        public void Apply(ITransaction tx, IDb basis)
        {
            if (destination.IsUnknown) return;

            var current = LibraryItem.Load(basis, item);
            var had = current.DestinationGames.ToHashSet();

            foreach (var game in destination.Games.Where(game => !had.Contains(game)))
                tx.Add(item, LibraryItem.DestinationGames, game);

            if (current.Contains(LibraryItem.DestinationUnknown))
                tx.Retract(item, LibraryItem.DestinationUnknown, Null.Instance);

            if (destination.Shared && had.Count == 0 && !current.Contains(LibraryItem.DestinationsShared))
                tx.Add(item, LibraryItem.DestinationsShared, Null.Instance);
        }

        public bool Equals(ITxFunction? other)
            => other is AddDestinationTxFunc add && add.Item == item && add.Destination.Equals(destination);

        private EntityId Item => item;
        private Destination Destination => destination;

        public override bool Equals(object? obj) => obj is ITxFunction other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(item, destination);
    }

    /// <inheritdoc cref="AddDestinationTxFunc"/>
    private sealed class RemoveDestinationTxFunc(EntityId item, GameId game) : ITxFunction
    {
        public void Apply(ITransaction tx, IDb basis)
        {
            var current = LibraryItem.Load(basis, item);
            var had = current.DestinationGames.ToHashSet();
            if (!had.Contains(game)) return;

            tx.Retract(item, LibraryItem.DestinationGames, game);

            if (had.Count == 1 && !current.Contains(LibraryItem.DestinationUnknown))
                tx.Add(item, LibraryItem.DestinationUnknown, Null.Instance);
        }

        public bool Equals(ITxFunction? other)
            => other is RemoveDestinationTxFunc remove && remove.Item == item && remove.Game == game;

        private EntityId Item => item;
        private GameId Game => game;

        public override bool Equals(object? obj) => obj is ITxFunction other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(item, game);
    }
}
