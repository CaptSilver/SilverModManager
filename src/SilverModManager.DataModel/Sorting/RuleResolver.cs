using System.Collections.Immutable;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;

namespace SilverModManager.DataModel.Sorting;

/// <summary>
/// An ordering constraint between two groups, as stored in a <see cref="LoadoutGroupRule"/> row or proposed by a
/// caller that has not written it yet.
/// </summary>
public record GroupRule(LoadoutItemGroupId Source, LoadoutItemGroupId Other, GroupRuleKind Kind);

/// <summary>
/// What a transaction changes about a loadout's groups and rules. A tx function only sees the database from before
/// its transaction, so anything the transaction adds or removes has to be handed to the resolver explicitly.
/// </summary>
public record RuleDelta
{
    /// <summary>No same-transaction changes.</summary>
    public static readonly RuleDelta Empty = new();

    /// <summary>Rules that are about to be written.</summary>
    public ImmutableArray<GroupRule> Candidates { get; init; } = [];

    /// <summary>Groups that are about to be created, appended at the end of the order in this sequence.</summary>
    public ImmutableArray<LoadoutItemGroupId> NewGroups { get; init; } = [];

    /// <summary>Groups that are about to be removed.</summary>
    public ImmutableArray<LoadoutItemGroupId> RemovedGroups { get; init; } = [];
}

/// <summary>
/// The order a <see cref="RuleResolver"/> settled on, or the cycle that made every order impossible
/// (<see cref="Order"/> is empty then).
/// </summary>
public readonly record struct RuleResolution(ImmutableArray<LoadoutItemGroupId> Order, RuleCycle? Cycle);

/// <summary>
/// Turns Before/After group rules into a total order, in ascending priority (the last group wins). Pure: it reads
/// nothing but its arguments, so a cycle is found before anything is written.
/// </summary>
/// <remarks>
/// This is a stable topological sort, not <see cref="Sorter"/>. The Sorter emits every item whose dependencies are
/// met in one batch, which moves unrelated groups past each other and reorders groups whose rules were already
/// satisfied. Here the group that is earliest in the current order goes first among those whose predecessors are
/// placed, so a group no rule touches stays put and an order that already satisfies the rules comes back unchanged.
/// </remarks>
public static class RuleResolver
{
    /// <summary>
    /// Orders <paramref name="currentOrder"/> (ascending priority) by <paramref name="rules"/> plus the changes in
    /// <paramref name="delta"/>. Rules that name a group outside the resulting set are ignored.
    /// </summary>
    public static RuleResolution Resolve(IReadOnlyList<LoadoutItemGroupId> currentOrder, IEnumerable<GroupRule> rules, RuleDelta? delta = null)
    {
        delta ??= RuleDelta.Empty;

        var removed = delta.RemovedGroups.ToHashSet();
        var items = new List<LoadoutItemGroupId>(currentOrder.Count + delta.NewGroups.Length);
        var position = new Dictionary<LoadoutItemGroupId, int>();
        foreach (var id in currentOrder.Concat(delta.NewGroups))
        {
            if (removed.Contains(id) || position.ContainsKey(id)) continue;
            position.Add(id, items.Count);
            items.Add(id);
        }

        // An edge runs from the group that must come first to the one that must come later.
        var edges = new List<(LoadoutItemGroupId From, LoadoutItemGroupId To)>();
        foreach (var rule in rules.Concat(delta.Candidates))
        {
            if (!position.ContainsKey(rule.Source) || !position.ContainsKey(rule.Other)) continue;
            edges.Add(rule.Kind == GroupRuleKind.After ? (rule.Other, rule.Source) : (rule.Source, rule.Other));
        }

        var successors = items.ToDictionary(static id => id, static _ => new List<LoadoutItemGroupId>());
        var predecessors = items.ToDictionary(static id => id, static _ => new List<LoadoutItemGroupId>());
        var waitingOn = items.ToDictionary(static id => id, static _ => 0);
        foreach (var (from, to) in edges)
        {
            successors[from].Add(to);
            predecessors[to].Add(from);
            waitingOn[to]++;
        }

        var ready = new PriorityQueue<LoadoutItemGroupId, int>();
        foreach (var id in items.Where(id => waitingOn[id] == 0))
            ready.Enqueue(id, position[id]);

        var order = ImmutableArray.CreateBuilder<LoadoutItemGroupId>(items.Count);
        while (ready.TryDequeue(out var id, out _))
        {
            order.Add(id);
            foreach (var next in successors[id])
            {
                if (--waitingOn[next] == 0) ready.Enqueue(next, position[next]);
            }
        }

        if (order.Count == items.Count) return new RuleResolution(order.MoveToImmutable(), Cycle: null);

        var stuck = items.Where(id => waitingOn[id] > 0).ToHashSet();
        return new RuleResolution([], FindCycle(stuck, predecessors, delta.Candidates));
    }

    /// <summary>
    /// Rewrites rules that name a collection into rules between the mods it holds, so the resolver and
    /// <see cref="FindViolations"/> only ever see mods. A rule on a collection holds for each of its mods against
    /// each mod on the other side; a collection with no mods contributes nothing.
    /// </summary>
    /// <param name="collections">The mods of each collection, by collection group id, lowest priority first.</param>
    /// <returns>The mod-level rules, each with the position in <paramref name="rules"/> of the rule it came from.</returns>
    public static List<(GroupRule Rule, int Origin)> ExpandCollections(
        IReadOnlyList<GroupRule> rules,
        IReadOnlyDictionary<LoadoutItemGroupId, LoadoutItemGroupId[]> collections)
    {
        var expanded = new List<(GroupRule, int)>(rules.Count);
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            LoadoutItemGroupId[] sources = collections.TryGetValue(rule.Source, out var s) ? s : [rule.Source];
            LoadoutItemGroupId[] others = collections.TryGetValue(rule.Other, out var o) ? o : [rule.Other];
            foreach (var source in sources)
            {
                foreach (var other in others)
                {
                    // A mod ruled against the collection it sits in would otherwise be ordered against itself.
                    if (source == other) continue;
                    expanded.Add((rule with { Source = source, Other = other }, i));
                }
            }
        }

        return expanded;
    }

    /// <summary>
    /// The positions in <paramref name="rules"/> of the rules <paramref name="order"/> (ascending priority) breaks.
    /// A rule naming a group outside the order is not broken, since the resolver ignores it too.
    /// </summary>
    public static List<int> FindViolations(IReadOnlyList<LoadoutItemGroupId> order, IReadOnlyList<GroupRule> rules)
    {
        var position = new Dictionary<LoadoutItemGroupId, int>(order.Count);
        for (var i = 0; i < order.Count; i++) position.TryAdd(order[i], i);

        var violated = new List<int>();
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (!position.TryGetValue(rule.Source, out var source) || !position.TryGetValue(rule.Other, out var other)) continue;

            // The last group wins, so After needs the source at the higher position.
            var holds = rule.Kind == GroupRuleKind.After ? source > other : source < other;
            if (!holds) violated.Add(i);
        }

        return violated;
    }

    /// <summary>
    /// Every group left over when the sort stalls has a predecessor that is also left over, so walking predecessors
    /// from any of them must come back to a group already visited, and that loop is a cycle. When one of the
    /// proposed rules is an edge of the loop it is the one reported, since it is the change the caller asked for.
    /// </summary>
    private static RuleCycle FindCycle(
        HashSet<LoadoutItemGroupId> stuck,
        Dictionary<LoadoutItemGroupId, List<LoadoutItemGroupId>> predecessors,
        ImmutableArray<GroupRule> candidates)
    {
        var walk = new List<LoadoutItemGroupId>();
        var seenAt = new Dictionary<LoadoutItemGroupId, int>();
        var current = stuck.First();
        while (!seenAt.ContainsKey(current))
        {
            seenAt.Add(current, walk.Count);
            walk.Add(current);
            current = predecessors[current].First(stuck.Contains);
        }

        // walk[i] has walk[i + 1] as a predecessor, and the last one has walk[start] as its predecessor.
        var loop = walk.Skip(seenAt[current]).ToList();
        for (var i = 0; i < loop.Count; i++)
        {
            var to = loop[i];
            var from = loop[(i + 1) % loop.Count];
            foreach (var candidate in candidates)
            {
                var (candidateFrom, candidateTo) = candidate.Kind == GroupRuleKind.After
                    ? (candidate.Other, candidate.Source)
                    : (candidate.Source, candidate.Other);
                if (candidateFrom == from && candidateTo == to) return new RuleCycle(candidate.Source, candidate.Other);
            }
        }

        return loop.Count == 1 ? new RuleCycle(loop[0], loop[0]) : new RuleCycle(loop[1 % loop.Count], loop[0]);
    }
}
