namespace SilverModManager.Games.Bethesda.Loot.Sorting;

// Follows libloot's sorting/plugins.rs (add_overlap_edges).

internal sealed partial class PluginsGraph<T>
{
    /// <summary>
    /// Adds an edge between plugins that override the same records or load the same assets, so that
    /// the plugin with more of them loads first. Record overlap takes precedence over asset overlap.
    /// An edge that is not needed, or would close a cycle, is left out.
    /// </summary>
    /// <param name="useIndex">
    /// Find the pairs to look at through an index of the records and files each plugin has, when every plugin
    /// can supply them, instead of looking at every pair. The edges are the same either way.
    /// </param>
    /// <returns>Whether any edge was added.</returns>
    public bool AddOverlapEdges(bool useIndex = true)
    {
        var added = false;
        var index = useIndex ? OverlapIndex.TryBuild(this) : null;

        for (var node = 0; node < NodeCount; node++)
        {
            var plugin = _graph[node];
            var pluginAssetCount = plugin.AssetCount;

            if (plugin.OverrideRecordCount == 0 && pluginAssetCount == 0) continue;

            // Masters and non-masters are sorted separately now, so this should add nothing for
            // them, but it stays as a safety net.
            foreach (var (other, mayOverlapRecords, mayOverlapAssets) in index?.PartnersAfter(node) ?? EveryPairWith(node))
            {
                var otherPlugin = _graph[other];

                // Only direct edges are checked, not paths, for speed.
                if (_graph.FindEdge(node, other) is not null || _graph.FindEdge(other, node) is not null) continue;

                // Records matter more than assets: assets are only compared when the plugins do not
                // overlap in records, or override as many records as each other.
                bool outerLoadsFirst;
                EdgeType edgeType;

                if (plugin.OverrideRecordCount == otherPlugin.OverrideRecordCount || !(mayOverlapRecords && plugin.DoRecordsOverlap(otherPlugin)))
                {
                    var otherAssetCount = otherPlugin.AssetCount;
                    if (pluginAssetCount == otherAssetCount || !(mayOverlapAssets && plugin.DoAssetsOverlap(otherPlugin))) continue;

                    outerLoadsFirst = pluginAssetCount > otherAssetCount;
                    edgeType = EdgeType.AssetOverlap;
                }
                else
                {
                    outerLoadsFirst = plugin.OverrideRecordCount > otherPlugin.OverrideRecordCount;
                    edgeType = EdgeType.RecordOverlap;
                }

                var (from, to) = outerLoadsFirst ? (node, other) : (other, node);

                if (IsPathCached(from, to) || PathExists(to, from)) continue;

                AddEdge(from, to, edgeType);
                added = true;
            }
        }

        return added;
    }

    private IEnumerable<(int Other, bool MayOverlapRecords, bool MayOverlapAssets)> EveryPairWith(int node)
    {
        for (var other = node + 1; other < NodeCount; other++) yield return (other, true, true);
    }

    /// <summary>
    /// Which plugins share a record or an archive file with which, so a pair that shares neither is never
    /// compared. A pair that shares a key is only a candidate: the plugin's own overlap answer still decides.
    /// </summary>
    private sealed class OverlapIndex
    {
        private const byte Records = 1;
        private const byte Assets = 2;

        private readonly Dictionary<ulong, List<int>> _recordPostings = [];
        private readonly Dictionary<ulong, List<int>> _assetPostings = [];
        private readonly IOverlapKeys[] _keyed;
        private readonly int[] _seen;
        private readonly byte[] _shared;

        private OverlapIndex(int nodeCount)
        {
            _keyed = new IOverlapKeys[nodeCount];
            _seen = new int[nodeCount];
            _shared = new byte[nodeCount];
            Array.Fill(_seen, -1);
        }

        public static OverlapIndex? TryBuild(PluginsGraph<T> graph)
        {
            var index = new OverlapIndex(graph.NodeCount);
            for (var node = 0; node < graph.NodeCount; node++)
            {
                if (graph[node].Plugin is not IOverlapKeys keyed) return null;

                index._keyed[node] = keyed;
                Post(index._recordPostings, keyed.RecordKeys, node);
                Post(index._assetPostings, keyed.AssetKeys, node);
            }

            return index;
        }

        // Nodes arrive in order, so each posting list stays sorted; a key a plugin repeats is listed once.
        private static void Post(Dictionary<ulong, List<int>> postings, IReadOnlyList<ulong> keys, int node)
        {
            foreach (var key in keys)
            {
                if (!postings.TryGetValue(key, out var posting)) postings[key] = posting = [];
                if (posting.Count == 0 || posting[^1] != node) posting.Add(node);
            }
        }

        /// <summary>
        /// The nodes after <paramref name="node"/> that share a key with it, lowest first, and whether they share
        /// a record key and whether they share an asset key. A kind that is not shared cannot overlap.
        /// </summary>
        public List<(int Other, bool MayOverlapRecords, bool MayOverlapAssets)> PartnersAfter(int node)
        {
            var partners = new List<int>();
            var possible = _seen.Length - node - 1;
            Collect(_recordPostings, _keyed[node].RecordKeys, node, Records, partners, possible);
            Collect(_assetPostings, _keyed[node].AssetKeys, node, Assets, partners, possible);

            partners.Sort();
            return [.. partners.Select(other => (other, (_shared[other] & Records) != 0, (_shared[other] & Assets) != 0))];
        }

        private void Collect(Dictionary<ulong, List<int>> postings, IReadOnlyList<ulong> keys, int node, byte kind, List<int> partners, int possible)
        {
            // Once every later node is known to share this kind there is nothing more to learn from it.
            var sharedWithAll = 0;
            foreach (var key in keys)
            {
                var posting = postings[key];
                for (var i = posting.BinarySearch(node) + 1; i < posting.Count; i++)
                {
                    var other = posting[i];
                    if (_seen[other] != node)
                    {
                        _seen[other] = node;
                        _shared[other] = 0;
                        partners.Add(other);
                    }

                    if ((_shared[other] & kind) == 0)
                    {
                        _shared[other] |= kind;
                        sharedWithAll++;
                    }
                }

                if (sharedWithAll == possible) return;
            }
        }
    }
}
