using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;

namespace NexusMods.Games.CreationEngine.SortOrder;

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
    /// Writes the position of each plugin in <paramref name="names"/> (index in the list) into
    /// <paramref name="tx"/>, creating an enabled row for a name that has none. Rows of plugins not named are left
    /// as they are, so pass the complete visible order.
    /// </summary>
    void StageOrder(SortOrderId sortOrderId, IReadOnlyList<string> names, ITransaction tx);

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
