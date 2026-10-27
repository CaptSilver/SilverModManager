using System.ComponentModel;
using System.Reactive.Concurrency;
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
/// lowest priority (GreaterIndexWins, winner at the bottom, as on Rules > File Conflicts). It stores nothing: the SortOrderId it hands out is the loadout's
/// own id, which LoadOrderViewModel only passes back here. Moves go through ILoadoutManager, so a move a stored rule
/// forbids is refused with the rule's text, as on Rules > File Conflicts.
/// </summary>
public sealed class ModPriorityOrderVariety(IConnection connection, ILoadoutManager loadouts, ILogger<ModPriorityOrderVariety> logger)
    : ISortOrderVariety<SortItemKey<EntityId>, ModPriorityReactiveSortItem>
{
    public static readonly SortOrderVarietyId StaticVarietyId = SortOrderVarietyId.From(new Guid("7f3b1c2e-9a44-4d6b-8e21-5c0f9a7d3b16"));

    public SortOrderVarietyId SortOrderVarietyId => StaticVarietyId;

    public ListSortDirection SortDirectionDefault => ListSortDirection.Ascending;

    public IndexOverrideBehavior IndexOverrideBehavior => IndexOverrideBehavior.GreaterIndexWins;

    public SortOrderUiMetadata SortOrderUiMetadata { get; } = new()
    {
        SortOrderName = "Load Order",
        OverrideInfoTitle = "Load Order - Last Wins",
        OverrideInfoMessage = """
                              When two mods ship the same file, the one lower in this list is used. Dragging a mod up or down changes which one wins, the same as on File Conflicts.
                              A rule you stored on the Rules tab always has the last word: a drag that would break one is refused.
                              """,
        WinnerIndexToolTip = "Last Wins: a mod lower in the list overrides the ones above it.",
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

    // Rebuilt from the database rather than from the priority rows alone: a mod's name and enabled state show on the
    // page too. A revision is read on the task pool, a burst of them (a bulk install) as one read, and one that wrote
    // none of the attributes the page shows is skipped. A re-read that finds the same rows (another loadout's commit)
    // is not a change. The subscription's first value is the current revision.
    public IObservable<IChangeSet<ModPriorityReactiveSortItem, SortItemKey<EntityId>>> GetSortOrderItemsChangeSet(SortOrderId sortOrderId)
    {
        var loadoutId = LoadoutOf(sortOrderId);
        var attributes = connection.Db.AttributeCache;
        HashSet<AttributeId> shown =
        [
            attributes.GetAttributeId(LoadoutItemGroupPriority.Priority.Id),
            attributes.GetAttributeId(LoadoutItemGroupPriority.Target.Id),
            attributes.GetAttributeId(LoadoutItemGroupPriority.Loadout.Id),
            attributes.GetAttributeId(LoadoutItem.Name.Id),
            attributes.GetAttributeId(LoadoutItem.Disabled.Id),
            attributes.GetAttributeId(LoadoutItem.Parent.Id),
        ];

        var revisions = connection.Revisions
            .Publish(shared => shared.Take(1).Merge(
                shared.Skip(1)
                    .Where(db => TouchesShownAttributes(db, shown))
                    .Throttle(RevisionThrottle, TaskPoolScheduler.Default)));

        // A cache applies only what differs from the previous read, so a re-read that finds the same rows notifies nobody.
        return ObservableChangeSet.Create<ModPriorityReactiveSortItem, SortItemKey<EntityId>>(
            cache => revisions.Subscribe(db =>
            {
                // This runs on the task pool for all but the first read, where an escaping exception would end the
                // process. A failed read is logged and the page keeps the rows it has; the next revision tries again.
                try
                {
                    var items = ReadItems(db, loadoutId);
                    cache.Edit(updater =>
                    {
                        var keys = items.Select(static item => item.Key).ToHashSet();
                        updater.Remove(updater.Keys.Where(key => !keys.Contains(key)).ToList());
                        updater.AddOrUpdate(items, ItemComparer.Instance);
                    });
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Reading the load order of loadout {Loadout} failed; the page keeps its last rows", loadoutId);
                }
            }),
            static item => item.Key);
    }

    /// <summary>How a revision is turned into rows; replaced in tests to make a read fail.</summary>
    internal Func<IDb, LoadoutId, List<ModPriorityReactiveSortItem>> ReadItems { get; init; } = ItemsOf;

    private static readonly TimeSpan RevisionThrottle = TimeSpan.FromMilliseconds(100);

    private static bool TouchesShownAttributes(IDb db, HashSet<AttributeId> shown)
    {
        foreach (var datom in db.RecentlyAdded)
        {
            if (shown.Contains(datom.A)) return true;
        }
        return false;
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
        var dragged = itemsToMove.Select(static key => key.Key).ToArray();

        // The list shows the lowest priority first, so a drop before the target is below it in priority.
        var inPriorityTerms = relativePosition == TargetRelativePosition.BeforeTarget ? TargetRelativePosition.AfterTarget : TargetRelativePosition.BeforeTarget;
        var (moved, loser) = Plan(highestFirst, dragged, dropTargetItem.Key, inPriorityTerms);
        if (moved.Length == 0)
        {
            // The target is one of the dragged rows: the order stays as it is.
            logger.LogTrace("Dropped rows onto one of themselves in loadout {Loadout}; nothing to move", LoadoutOf(sortOrderId));
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
        // Index order, lowest priority first: a positive delta moves the row toward the winning end.
        var rows = PriorityRows(db ?? connection.Db, LoadoutOf(sortOrderId));
        rows.Reverse();
        var from = rows.FindIndex(row => row.Value == sourceItem.Key);
        if (from < 0) throw new InvalidOperationException("The mod you moved is no longer in the load order. Check the list and try again.");

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
    /// Works out the call to ILoadoutManager for a drop, in priority terms: <paramref name="highestFirst"/> is the rows
    /// highest priority first, so dropping before the
    /// target puts the moved rows over it (the target becomes the loser), and dropping after it puts them over the
    /// next row down that is not itself moving, or at the bottom when there is none.
    /// </summary>
    /// <returns>
    /// The moved rows in list order, and the row they land over. No moved rows when the target is among the dragged
    /// ones. Throws when none of the dragged rows, or the target, is in the order any more (the list was stale).
    /// </returns>
    internal static (LoadoutItemGroupPriorityId[] Moved, LoadoutItemGroupPriorityId? Loser) Plan(
        IReadOnlyList<LoadoutItemGroupPriorityId> highestFirst,
        EntityId[] moved,
        EntityId target,
        TargetRelativePosition position)
    {
        var movedSet = moved.ToHashSet();
        var movedRows = highestFirst.Where(row => movedSet.Contains(row.Value)).ToArray();
        if (movedRows.Length == 0)
            throw new InvalidOperationException("The mods you moved are no longer in the load order. Check the list and try again.");

        var targetIndex = highestFirst.ToList().FindIndex(row => row.Value == target);
        if (targetIndex < 0)
            throw new InvalidOperationException("The mod you dropped on is no longer in the load order. Check the list and try again.");

        if (movedSet.Contains(target)) return ([], null);
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

    // Lowest priority first, so the index grows toward the winner.
    private static List<ModPriorityReactiveSortItem> ItemsOf(IDb db, LoadoutId loadoutId) => LoadoutItemGroupPriority
        .FindByLoadout(db, loadoutId)
        .Where(static row => row.Target.IsValid())
        .OrderBy(static row => row.Priority)
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
