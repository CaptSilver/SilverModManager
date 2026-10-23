using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Larian.BaldursGate3.SortOrder;

/// <summary>
/// What the pak load order offers beyond <see cref="ISortOrderVariety"/>: switching a pak on and off, and the
/// staging calls the deployment writers use to put a changed order or switch into a transaction of their own.
/// </summary>
public interface IBg3PakSortOrderVariety : ISortOrderVariety
{
    /// <summary>
    /// Switches one pak on or off. Throws when the pak has no row in the sort order, or when the game installed
    /// it: the switch of such a pak is its group's.
    /// </summary>
    Task SetEnabled(SortOrderId sortOrderId, SortItemKey<string> item, bool enabled, IDb? db = null, CancellationToken token = default);

    /// <summary>
    /// The stored rows of the loadout's pak order as of the loadout's own revision, in order, for the file the
    /// game's mod list is written from. A pak without a row is not listed, and a loadout without an order gives
    /// an empty list.
    /// </summary>
    IReadOnlyList<Bg3PakSortData> StoredOrder(Loadout.ReadOnly loadout);

    /// <summary>
    /// Writes the position of each pak in <paramref name="names"/> (index in the list) into
    /// <paramref name="tx"/>, creating an enabled row for a name that has none. Rows of paks not named are left
    /// as they are, so pass the complete visible order.
    /// </summary>
    void StageOrder(SortOrderId sortOrderId, IReadOnlyList<string> names, ITransaction tx);

    /// <summary>
    /// <see cref="StageOrder(SortOrderId, IReadOnlyList{string}, ITransaction)"/> with a switch for each name; a
    /// null switch leaves an existing row as it is and starts a new one on. A row staged for a pak the loadout
    /// does not have yet is kept hidden and takes its place and switch when the pak arrives.
    /// </summary>
    void StageOrder(SortOrderId sortOrderId, IReadOnlyList<(string Name, bool? IsEnabled)> entries, ITransaction tx);

    /// <summary>
    /// Writes the on/off state of an existing pak row into <paramref name="tx"/>. Throws when the sort order has
    /// no row for <paramref name="name"/>, so stage the order in an earlier transaction when the row is new.
    /// </summary>
    void StageEnabled(SortOrderId sortOrderId, string name, bool enabled, ITransaction tx);

    /// <summary>
    /// Clears the notice that the app replaced the game's mod list, if there is one. The notice entity stays when
    /// it also holds the lists of paks the app wrote.
    /// </summary>
    Task ClearReplacedGameEdit(SortOrderId sortOrderId);

    /// <summary>
    /// Forgets that the game threw the mod list away, so the app writes its own list again. Any change the user
    /// makes to the order or a switch does this on its own.
    /// </summary>
    Task ClearModListReset(SortOrderId sortOrderId);
}
