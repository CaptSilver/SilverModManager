using System.Collections.Immutable;
using System.Diagnostics;
using DynamicData.Kernel;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.DataModel.Sorting;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel;

internal partial class LoadoutManager
{
    /// <summary>
    /// Rebalances the priorities of all items in a loadout.
    /// </summary>
    private class RebalancePrioritiesTxFunc : ITxFunction
    {
        private readonly LoadoutId _loadoutId;
        private readonly EntityId[] _toSkip;

        public RebalancePrioritiesTxFunc(LoadoutId loadoutId, EntityId[] toSkip)
        {
            _loadoutId = loadoutId;

            Array.Sort(toSkip);
            _toSkip = toSkip;
        }

        public bool Equals(ITxFunction? obj)
        {
            if (obj is not RebalancePrioritiesTxFunc other) return false;
            return other._loadoutId.Equals(_loadoutId);
        }

        public override int GetHashCode() => _loadoutId.GetHashCode();

        public void Apply(ITransaction tx, IDb basis)
        {
            var priorities = LoadoutItemGroupPriority
                .FindByLoadout(basis, _loadoutId)
                .OrderBy(static model => model.Priority);

            var start = 0UL;
            foreach (var model in priorities)
            {
                var index = Array.BinarySearch(_toSkip, model.TargetId);
                if (index >= 0) continue;

                if (!model.Target.IsValid())
                {
                    tx.Delete(model.Id, recursive: false);
                    continue;
                }

                var newPriority = ConflictPriority.From(++start);
                tx.Add(model.Id, LoadoutItemGroupPriority.Priority, newPriority);
            }
        }
    }

    private class ResolveFileConflictsTxFunc : ITxFunction
    {
        private readonly LoadoutId _loadoutId;
        private readonly LoadoutItemGroupPriorityId[] _winnerIds;
        private readonly Optional<LoadoutItemGroupPriorityId> _loserId;

        public ResolveFileConflictsTxFunc(LoadoutId loadoutId, LoadoutItemGroupPriorityId[] winnerIds, Optional<LoadoutItemGroupPriorityId> loserId)
        {
            _loadoutId = loadoutId;
            _winnerIds = winnerIds;
            _loserId = loserId;
        }

        public bool Equals(ITxFunction? obj) => obj is ResolveFileConflictsTxFunc other && other._loadoutId == _loadoutId;
        public override int GetHashCode() => _loadoutId.GetHashCode();

        public void Apply(ITransaction tx, IDb basis)
        {
            var priorities = LoadoutItemGroupPriority
                .FindByLoadout(basis, _loadoutId)
                .OrderBy(static model => model.Priority)
                .ToList();

            var items = priorities
                .Where(x => _winnerIds.Contains(x.LoadoutItemGroupPriorityId))
                .ToArray();

            var loserIndex = _loserId.Convert(id => priorities.FindIndex(other => other.Id == id.Value));
 
            // remove items
            foreach (var item in items)
            {
                var index = priorities.FindIndex(other => other.Id == item.Id);
                Debug.Assert(index != -1, "should be an existing model");
                priorities.RemoveAt(index);

                if (loserIndex.HasValue && loserIndex.Value >= index) loserIndex = loserIndex.Value - 1;
            }

            // insert items
            var insertIndex = loserIndex.HasValue ? loserIndex.Value + 1 : 0;

            Debug.Assert(insertIndex >= 0 || insertIndex < priorities.Count);
            priorities.InsertRange(index: insertIndex, items);

            // recalculate new priorities
            var start = 0UL;
            foreach (var model in priorities)
            {
                var newPriority = ConflictPriority.From(++start);
                tx.Add(model.Id, LoadoutItemGroupPriority.Priority, newPriority);
            }
        }
    }

    /// <summary>
    /// Adds a priority to an item.
    /// </summary>
    internal class AddPriorityTxFunc : ITxFunction
    {
        private readonly LoadoutId _loadoutId;
        private readonly LoadoutItemGroupId _targetId;

        public AddPriorityTxFunc(LoadoutId loadoutId, LoadoutItemGroupId targetId)
        {
            _loadoutId = loadoutId;
            _targetId = targetId;
        }

        public bool Equals(ITxFunction? obj)
        {
            if (obj is not AddPriorityTxFunc other) return false;
            return other._loadoutId.Equals(_loadoutId) && other._targetId.Equals(_targetId);
        }

        public override int GetHashCode() => HashCode.Combine(_loadoutId, _targetId);

        public void Apply(ITransaction tx, IDb basis)
        {
            var priority = GetNextPriority(_loadoutId, basis);
            var id = tx.TempId();
            tx.Add(id, LoadoutItemGroupPriority.Loadout, _loadoutId);
            tx.Add(id, LoadoutItemGroupPriority.Target, _targetId);
            tx.Add(id, LoadoutItemGroupPriority.Priority, priority);
        }
    }

    /// <summary>
    /// Rewrites a loadout's group priorities so the stored rules, plus the changes in <paramref name="delta"/>, hold.
    /// </summary>
    /// <remarks>
    /// A tx function only sees the database from before its transaction, and every function in a transaction gets the
    /// same one, so two resolvers for one loadout would each compute a whole order over it and write the same rows.
    /// Equality is therefore the loadout alone: the method that owns the transaction builds one complete delta (every
    /// candidate rule, new group and removed group) and adds one function. Groups the delta creates get their
    /// priority row from here, since a row another function created under its own temp id can't be addressed.
    /// </remarks>
    internal class ApplyGroupRulesTxFunc(LoadoutId loadoutId, RuleDelta delta) : ITxFunction
    {
        public bool Equals(ITxFunction? other) => other is ApplyGroupRulesTxFunc func && func.LoadoutId == loadoutId;

        public override int GetHashCode() => loadoutId.GetHashCode();

        private LoadoutId LoadoutId => loadoutId;

        public void Apply(ITransaction tx, IDb basis)
        {
            var rows = LoadoutItemGroupPriority
                .FindByLoadout(basis, loadoutId)
                .OrderBy(static model => model.Priority)
                .ToList();

            var removed = delta.RemovedGroups.ToHashSet();
            var rowOfGroup = new Dictionary<LoadoutItemGroupId, LoadoutItemGroupPriority.ReadOnly>();
            foreach (var row in rows)
            {
                if (removed.Contains(row.TargetId)) continue;

                if (!row.Target.IsValid())
                {
                    tx.Delete(row.Id, recursive: false);
                    continue;
                }

                rowOfGroup[row.TargetId] = row;
            }

            var storedRules = LoadoutGroupRule
                .FindByLoadout(basis, loadoutId)
                .Select(static rule => new GroupRule(rule.SourceId, rule.OtherId, rule.Kind));

            var resolution = RuleResolver.Resolve(rowOfGroup.Keys.OrderBy(group => rowOfGroup[group].Priority).ToArray(), storedRules, delta);
            if (resolution.Cycle is { } cycle)
                throw new InvalidOperationException($"The group rules of loadout {loadoutId.Value} contradict each other: {cycle.First.Value} and {cycle.Second.Value}.");

            var next = 0UL;
            foreach (var group in resolution.Order)
            {
                var priority = ConflictPriority.From(++next);
                if (rowOfGroup.TryGetValue(group, out var row))
                {
                    if (row.Priority != priority) tx.Add(row.Id, LoadoutItemGroupPriority.Priority, priority);
                    continue;
                }

                var id = tx.TempId();
                tx.Add(id, LoadoutItemGroupPriority.Loadout, loadoutId);
                tx.Add(id, LoadoutItemGroupPriority.Target, group);
                tx.Add(id, LoadoutItemGroupPriority.Priority, priority);
            }
        }
    }

    private class ApplyCollectionRulesTxFunc : ITxFunction
    {
        private readonly LoadoutId _loadoutId;
        private readonly ImmutableArray<EntityId> _sortedItems;

        public ApplyCollectionRulesTxFunc(LoadoutId loadoutId, ImmutableArray<EntityId> sortedItems)
        {
            _loadoutId = loadoutId;
            _sortedItems = sortedItems;
        }

        // One per loadout: a bare type check would collapse the functions of two loadouts in one transaction.
        public bool Equals(ITxFunction? other) => other is ApplyCollectionRulesTxFunc func && func._loadoutId == _loadoutId;

        public override int GetHashCode() => _loadoutId.GetHashCode();

        public void Apply(ITransaction tx, IDb basis)
        {
            var priorities = LoadoutItemGroupPriority
                .FindByLoadout(basis, _loadoutId)
                .OrderBy(static model => model.Priority)
                .ToList();

            var prioritiesOfSortedItems = new List<LoadoutItemGroupPriority.ReadOnly>(capacity: _sortedItems.Length);
            foreach (var itemId in _sortedItems)
            {
                var index = priorities.FindIndex(priority => priority.TargetId.Value == itemId);
                if (index == -1)
                {
                    // TODO: add new priority, blocked on tx rework
                    continue;
                }

                var priority = priorities[index];
                priorities.RemoveAt(index);

                prioritiesOfSortedItems.Add(priority);
            }

            priorities.AddRange(prioritiesOfSortedItems);

            var start = 0UL;
            foreach (var model in priorities)
            {
                var newPriority = ConflictPriority.From(++start);
                tx.Add(model.Id, LoadoutItemGroupPriority.Priority, newPriority);
            }
        }
    }
}
