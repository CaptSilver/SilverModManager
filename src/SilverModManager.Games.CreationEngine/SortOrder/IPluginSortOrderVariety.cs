using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.SortOrder;

/// <summary>
/// What the plugin load order offers beyond <see cref="ISortOrderVariety"/>: switching a plugin on and off, and the
/// staging calls the deployment writers use to put a changed order or switch into a transaction of their own.
/// </summary>
public interface IPluginSortOrderVariety : ISortOrderVariety
{
    /// <summary>
    /// Switches one plugin on or off. Throws when the plugin has no row in the sort order, or when the game always
    /// loads it, as it does the early loaders.
    /// </summary>
    Task SetEnabled(SortOrderId sortOrderId, SortItemKey<string> item, bool enabled, IDb? db = null, CancellationToken token = default);

    /// <summary>
    /// The stored rows of the loadout's plugin order as of the loadout's own revision, in order, for the files the
    /// game's plugin list is written from. A plugin without a row is not listed, and a loadout without an order
    /// gives an empty list.
    /// </summary>
    IReadOnlyList<PluginSortData> StoredOrder(Loadout.ReadOnly loadout);

    /// <summary>
    /// Writes the position of each plugin in <paramref name="names"/> (index in the list) into
    /// <paramref name="tx"/>, creating an enabled row for a name that has none. Rows of plugins not named are left
    /// as they are, so pass the complete visible order.
    /// </summary>
    void StageOrder(SortOrderId sortOrderId, IReadOnlyList<string> names, ITransaction tx);

    /// <summary>
    /// <see cref="StageOrder(SortOrderId, IReadOnlyList{string}, ITransaction)"/> with a switch for each name; a
    /// null switch leaves an existing row as it is and starts a new one on. A row staged for a plugin the loadout
    /// does not have yet is kept hidden and takes its place and switch when the plugin arrives.
    /// </summary>
    void StageOrder(SortOrderId sortOrderId, IReadOnlyList<(string Name, bool? IsEnabled)> entries, ITransaction tx);

    /// <summary>
    /// Writes the on/off state of an existing plugin row into <paramref name="tx"/>. Throws when the sort order has
    /// no row for <paramref name="name"/>, so stage the order in an earlier transaction when the row is new.
    /// </summary>
    void StageEnabled(SortOrderId sortOrderId, string name, bool enabled, ITransaction tx);

    /// <summary>
    /// Deletes the notice that the app replaced the game's plugin list, if there is one.
    /// </summary>
    Task ClearReplacedGameEdit(SortOrderId sortOrderId);
}
