namespace SilverModManager.Games.Bethesda.Loot.Sorting;

// Follows libloot's sorting/plugins.rs (add_tie_break_edges, pin_node_position).

internal sealed partial class PluginsGraph<T>
{
    /// <summary>
    /// Adds edges until a single path runs through every plugin, so that there is only one possible
    /// topological sort. The plugins are walked in their current load order and each consecutive
    /// pair gets an edge from the earlier to the later unless that would make a cycle.
    /// </summary>
    /// <remarks>
    /// Where a pair (A, B) cannot be joined because B must load before A through a path B -> ... -> A,
    /// each plugin on that path is pinned: placed after the last plugin of the new load order so far
    /// that it does not have to load before. Pinned plugins are kept in <c>newLoadOrder</c>, because
    /// walking the old load order again would miss the plugins pinned by earlier paths and leave
    /// several paths through the graph.
    /// </remarks>
    /// <exception cref="PathfindingException">A path search met a node it cannot trace back.</exception>
    public void AddTieBreakEdges()
    {
        var newLoadOrder = new List<int>();
        var processed = new HashSet<int>();

        // Stable, so that plugins with equal indices stay in node order as Rust's sort_by_key does.
        var nodes = Enumerable.Range(0, NodeCount).OrderBy(n => _graph[n].LoadOrderIndex).ToList();

        for (var i = 1; i < nodes.Count; i++)
        {
            var current = nodes[i - 1];
            var next = nodes[i];

            var foundPath = FindPath(next, current);
            if (foundPath is null)
            {
                // Nothing forces next before current, so next can load after it.
                AddEdge(current, next, EdgeType.TieBreak);

                // current may already be placed, if it was on a path pinned for an earlier pair.
                if (processed.Add(current))
                {
                    newLoadOrder.Add(current);
                }
                else if (newLoadOrder[^1] != current)
                {
                    // current is not last, so next has no defined position relative to the plugins
                    // after current and has to be pinned too, or there would be several paths.
                    PinNodePosition(processed, newLoadOrder, next, 0);
                }

                continue;
            }

            var pathFromNext = foundPath.ToList();

            if (nodes[0] == current)
            {
                // The path ends at the first plugin checked: it is the start of the new load
                // order, and there is nothing for it to load after.
                foreach (var node in pathFromNext)
                {
                    newLoadOrder.Add(node);
                    processed.Add(node);
                }

                continue;
            }

            // current is the last node of the path and is appended below instead.
            pathFromNext.RemoveAt(pathFromNext.Count - 1);

            // Each plugin of the path is positioned relative to ones already in the new load order.
            // Nothing in the path can load before the previous plugin pinned from it, so that
            // position is where the next search stops.
            var rangeStart = 0;
            foreach (var node in pathFromNext) rangeStart = PinNodePosition(processed, newLoadOrder, node, rangeStart);

            if (processed.Add(current)) newLoadOrder.Add(current);
        }
    }

    /// <returns>The position in the new load order just after the pinned node, or the unchanged start if it was already pinned.</returns>
    private int PinNodePosition(HashSet<int> processed, List<int> newLoadOrder, int node, int rangeStart)
    {
        if (processed.Contains(node)) return rangeStart;

        // The last plugin this one does not have to load before. Searching for the last one it must
        // load after would walk further back along the path and is a lot slower.
        int? previous = null;
        for (var i = newLoadOrder.Count - 1; i >= rangeStart; i--)
        {
            if (PathExists(node, newLoadOrder[i])) continue;
            previous = i;
            break;
        }

        // The edge may be missing: only the absence of a path the other way is known.
        if (previous is { } p) AddEdge(newLoadOrder[p], node, EdgeType.TieBreak);

        var insertPosition = previous is { } q ? q + 1 : rangeStart;

        if (insertPosition < newLoadOrder.Count) AddEdge(node, newLoadOrder[insertPosition], EdgeType.TieBreak);

        newLoadOrder.Insert(insertPosition, node);
        processed.Add(node);

        return insertPosition + 1;
    }
}
