namespace SilverModManager.Games.Bethesda.Loot.Sorting;

/// <summary>Thrown by <see cref="Algorithms.TopologicalSort{TNode,TEdge}"/> when the graph has a cycle.</summary>
public sealed class GraphCycleException(int node) : Exception("The graph contains a cycle.")
{
    /// <summary>A node the cycle was detected at.</summary>
    public int Node { get; } = node;
}

/// <summary>Thrown by <see cref="Algorithms.BellmanFord{TNode,TEdge}"/> when a negative cycle is reachable.</summary>
public sealed class NegativeCycleException() : Exception("The graph contains a negative cycle.");

/// <summary>Shortest-path result: a distance and the preceding node, if any, for every node index.</summary>
public sealed record BellmanFordPaths(float[] Distances, int?[] Predecessors);

/// <summary>
/// Graph algorithms ported from petgraph 0.8.2 so that ties resolve the way libloot's do. They
/// walk the same edge orders as petgraph, newest edge first.
/// </summary>
public static class Algorithms
{
    /// <summary>
    /// Follows petgraph's <c>toposort</c> (algo/mod.rs): trees start in node index order, a node's
    /// neighbours are pushed newest first and so visited oldest first, and the finish order is reversed.
    /// </summary>
    public static IReadOnlyList<int> TopologicalSort<TNode, TEdge>(DirectedGraph<TNode, TEdge> graph)
    {
        var discovered = new bool[graph.NodeCount];
        var finished = new bool[graph.NodeCount];
        var stack = new Stack<int>();
        var finishStack = new List<int>();

        for (var i = 0; i < graph.NodeCount; i++)
        {
            if (discovered[i]) continue;
            stack.Push(i);
            while (stack.TryPeek(out var node))
            {
                if (!discovered[node])
                {
                    discovered[node] = true;
                    foreach (var edge in graph.OutEdgesNewestFirst(node))
                    {
                        if (edge.Target == node) throw new GraphCycleException(node);
                        if (!discovered[edge.Target]) stack.Push(edge.Target);
                    }
                }
                else
                {
                    stack.Pop();
                    if (!finished[node])
                    {
                        finished[node] = true;
                        finishStack.Add(node);
                    }
                }
            }
        }

        finishStack.Reverse();

        // Walking backwards from each node in order, a node that reaches anything not yet seen
        // besides itself is part of a cycle.
        Array.Clear(discovered);
        foreach (var start in finishStack)
        {
            var seenStart = false;
            stack.Clear();
            stack.Push(start);
            while (stack.TryPop(out var node))
            {
                if (discovered[node]) continue;
                discovered[node] = true;
                if (seenStart) throw new GraphCycleException(node);
                seenStart = true;
                foreach (var edge in graph.InEdgesNewestFirst(node))
                {
                    if (!discovered[edge.Source]) stack.Push(edge.Source);
                }
            }
        }

        return finishStack;
    }

    /// <summary>
    /// Follows petgraph's <c>bellman_ford</c> (algo/bellman_ford.rs) with <c>f32</c> distances. Edges
    /// are relaxed node by node in index order with each node's edges newest first, and only a
    /// strictly shorter distance replaces a predecessor, so the first predecessor to reach a
    /// distance keeps it.
    /// </summary>
    public static BellmanFordPaths BellmanFord<TNode, TEdge>(
        DirectedGraph<TNode, TEdge> graph, int source, Func<TEdge, float> weight)
    {
        var count = graph.NodeCount;
        var predecessors = new int?[count];
        var distances = new float[count];
        Array.Fill(distances, float.PositiveInfinity);
        distances[source] = 0f;

        for (var round = 1; round < count; round++)
        {
            var didUpdate = false;
            for (var i = 0; i < count; i++)
            {
                foreach (var edge in graph.OutEdgesNewestFirst(i))
                {
                    var candidate = distances[i] + weight(edge.Weight);
                    if (!(candidate < distances[edge.Target])) continue;
                    distances[edge.Target] = candidate;
                    predecessors[edge.Target] = i;
                    didUpdate = true;
                }
            }

            if (!didUpdate) break;
        }

        for (var i = 0; i < count; i++)
        {
            foreach (var edge in graph.OutEdgesNewestFirst(i))
            {
                if (distances[i] + weight(edge.Weight) < distances[edge.Target]) throw new NegativeCycleException();
            }
        }

        return new BellmanFordPaths(distances, predecessors);
    }
}
