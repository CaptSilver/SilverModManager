using System.ComponentModel;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Kernel;
using Microsoft.Extensions.Logging;
using OneOf;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games;

/// <summary>
/// The Load Order page of a game with no sort-order variety: a view over LoadoutItemGroupPriority. Index 0 is the
/// highest priority (SmallerIndexWins, winner on top). It stores nothing: the SortOrderId it hands out is the loadout's
/// own id, which LoadOrderViewModel only passes back here. Moves go through ILoadoutManager, so a move a stored rule
/// forbids is refused with the rule's text, as on Rules > File Conflicts.
/// </summary>
public sealed class ModPriorityOrderVariety(IConnection connection, ILoadoutManager loadouts, ILogger<ModPriorityOrderVariety> logger)
    : ISortOrderVariety<SortItemKey<EntityId>, ModPriorityReactiveSortItem>
{
    public static readonly SortOrderVarietyId StaticVarietyId = SortOrderVarietyId.From(new Guid("7f3b1c2e-9a44-4d6b-8e21-5c0f9a7d3b16"));

    public SortOrderVarietyId SortOrderVarietyId => StaticVarietyId;

    public ListSortDirection SortDirectionDefault => ListSortDirection.Ascending;

    public IndexOverrideBehavior IndexOverrideBehavior => IndexOverrideBehavior.SmallerIndexWins;

    public SortOrderUiMetadata SortOrderUiMetadata { get; } = new()
    {
        SortOrderName = "Load Order",
        OverrideInfoTitle = "Load Order - Higher Wins",
        OverrideInfoMessage = """
                              When two mods ship the same file, the one higher in this list is used. Dragging a mod up or down changes which one wins, the same as on File Conflicts.
                              A rule you stored on the Rules tab always has the last word: a drag that would break one is refused.
                              """,
        WinnerIndexToolTip = "First Wins: a mod higher in the list overrides the ones below it.",
        IndexColumnHeader = "Load Order",
        DisplayNameColumnHeader = "Mod",
        EmptyStateMessageTitle = "No mods yet",
        EmptyStateMessageContents = "Mods appear here once the loadout has any.",
        LearnMoreUrl = "https://github.com/CaptSilver/SilverModManager#readme",
    };

    /// <summary>The loadout's id doubles as the handle: there is no sort-order entity to look up.</summary>
    private static SortOrderId HandleFor(LoadoutId loadoutId) => SortOrderId.From(loadoutId.Value);

    private static LoadoutId LoadoutOf(SortOrderId handle) => LoadoutId.From(handle.Value);

    public Optional<SortOrderId> GetSortOrderIdFor(OneOf<LoadoutId, CollectionGroupId> parentEntity, IDb? db = null)
        => parentEntity.Match(loadoutId => Optional<SortOrderId>.Create(HandleFor(loadoutId)), _ => Optional<SortOrderId>.None);

    public ValueTask<SortOrderId> GetOrCreateSortOrderFor(LoadoutId loadoutId, OneOf<LoadoutId, CollectionGroupId> parentEntity, CancellationToken token = default)
        => ValueTask.FromResult(HandleFor(loadoutId));

    public IReadOnlyList<ModPriorityReactiveSortItem> GetSortOrderItems(SortOrderId sortOrderId, IDb? db = null)
        => ItemsOf(db ?? connection.Db, LoadoutOf(sortOrderId));

    // Every revision, not only the priority rows': a mod's name and enabled state show on the page too.
    public IObservable<IChangeSet<ModPriorityReactiveSortItem, SortItemKey<EntityId>>> GetSortOrderItemsChangeSet(SortOrderId sortOrderId)
    {
        var loadoutId = LoadoutOf(sortOrderId);
        return connection.Revisions
            .Select(db => (IEnumerable<ModPriorityReactiveSortItem>)ItemsOf(db, loadoutId))
            .StartWith(ItemsOf(connection.Db, loadoutId))
            .EditDiff(static item => item.Key, ItemComparer.Instance);
    }

    public async Task MoveItems(
        SortOrderId sortOrderId,
        SortItemKey<EntityId>[] itemsToMove,
        SortItemKey<EntityId> dropTargetItem,
        TargetRelativePosition relativePosition,
        IDb? db = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var highestFirst = PriorityRows(db ?? connection.Db, LoadoutOf(sortOrderId));
        var (moved, loser) = Plan(highestFirst, itemsToMove.Select(static key => key.Key).ToArray(), dropTargetItem.Key, relativePosition);
        if (moved.Length == 0)
        {
            logger.LogTrace("Nothing to move in loadout {Loadout}: the dropped rows are the drop target or no longer exist", LoadoutOf(sortOrderId));
            return;
        }

        var result = loser is { } loserId
            ? await loadouts.ResolveFileConflicts(moved, loserId)
            : await loadouts.LoseAllFileConflicts(moved);
        if (result.IsRuleViolation)
            throw new InvalidOperationException($"That move would break the rule '{GroupRuleText.WinsOver(result.Rule)}'.");
    }

    public async Task MoveItemDelta(SortOrderId sortOrderId, SortItemKey<EntityId> sourceItem, int delta, IDb? db = null, CancellationToken token = default)
    {
        var rows = PriorityRows(db ?? connection.Db, LoadoutOf(sortOrderId));
        var from = rows.ToList().FindIndex(row => row.Value == sourceItem.Key);
        if (from < 0) throw new InvalidOperationException($"Row {sourceItem.Key} is not in the priority order of loadout {LoadoutOf(sortOrderId).Value}.");

        var to = Math.Clamp(from + delta, 0, rows.Count - 1);
        if (to == from) return;

        await MoveItems(sortOrderId, [sourceItem], new SortItemKey<EntityId>(rows[to].Value), to > from ? TargetRelativePosition.AfterTarget : TargetRelativePosition.BeforeTarget, db, token);
    }

    // The order lives in the priority rows, which the loadout keeps itself, so there is nothing here to bring up to date.
    public ValueTask ReconcileSortOrder(SortOrderId sortOrderId, IDb? db = null, CancellationToken token = default)
    {
        logger.LogTrace("Load order of loadout {Loadout} reads the priority rows directly: nothing to reconcile", LoadoutOf(sortOrderId));
        return ValueTask.CompletedTask;
    }

    // No sort-order entity was ever written, so there is nothing to delete.
    public ValueTask DeleteSortOrder(SortOrderId sortOrderId, CancellationToken token = default)
    {
        logger.LogTrace("Load order of loadout {Loadout} stores nothing: nothing to delete", LoadoutOf(sortOrderId));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Works out the call to ILoadoutManager for a drop. The list is highest priority first, so dropping before the
    /// target puts the moved rows over it (the target becomes the loser), and dropping after it puts them over the
    /// next row down that is not itself moving, or at the bottom when there is none.
    /// </summary>
    /// <returns>
    /// The moved rows in list order, and the row they land over. No moved rows when the target is among the dragged
    /// ones or none of the dragged rows is in the order.
    /// </returns>
    internal static (LoadoutItemGroupPriorityId[] Moved, LoadoutItemGroupPriorityId? Loser) Plan(
        IReadOnlyList<LoadoutItemGroupPriorityId> highestFirst,
        EntityId[] moved,
        EntityId target,
        TargetRelativePosition position)
    {
        var targetIndex = highestFirst.ToList().FindIndex(row => row.Value == target);
        if (targetIndex < 0) throw new InvalidOperationException($"Row {target} is not in the priority order.");

        var movedSet = moved.ToHashSet();
        if (movedSet.Contains(target)) return ([], null);

        var movedRows = highestFirst.Where(row => movedSet.Contains(row.Value)).ToArray();
        if (position == TargetRelativePosition.BeforeTarget) return (movedRows, highestFirst[targetIndex]);

        for (var i = targetIndex + 1; i < highestFirst.Count; i++)
        {
            if (!movedSet.Contains(highestFirst[i].Value)) return (movedRows, highestFirst[i]);
        }
        return (movedRows, null);
    }

    private static List<LoadoutItemGroupPriorityId> PriorityRows(IDb db, LoadoutId loadoutId) => LoadoutItemGroupPriority
        .FindByLoadout(db, loadoutId)
        .Where(static row => row.Target.IsValid())
        .OrderByDescending(static row => row.Priority)
        .Select(static row => row.LoadoutItemGroupPriorityId)
        .ToList();

    private static List<ModPriorityReactiveSortItem> ItemsOf(IDb db, LoadoutId loadoutId) => LoadoutItemGroupPriority
        .FindByLoadout(db, loadoutId)
        .Where(static row => row.Target.IsValid())
        .OrderByDescending(static row => row.Priority)
        .Select(static (row, index) =>
        {
            var item = row.Target.AsLoadoutItem();
            return new ModPriorityReactiveSortItem(index, row.Id, row.TargetId, item.Name, item.IsEnabled());
        })
        .ToList();

    private sealed class ItemComparer : IEqualityComparer<ModPriorityReactiveSortItem>
    {
        public static readonly ItemComparer Instance = new();

        public bool Equals(ModPriorityReactiveSortItem? x, ModPriorityReactiveSortItem? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.Key.Equals(y.Key) && x.SortIndex == y.SortIndex
                                      && x.ModName == y.ModName && x.IsActive == y.IsActive && x.ModGroupId.Equals(y.ModGroupId));

        public int GetHashCode(ModPriorityReactiveSortItem item) => item.Key.GetHashCode();
    }
}
