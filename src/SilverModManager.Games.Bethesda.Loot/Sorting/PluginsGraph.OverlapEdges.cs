namespace SilverModManager.Games.Bethesda.Loot.Sorting;

// Follows libloot's sorting/plugins.rs (add_overlap_edges).

internal sealed partial class PluginsGraph<T>
{
    /// <summary>
    /// Adds an edge between plugins that override the same records or load the same assets, so that
    /// the plugin with more of them loads first. Record overlap takes precedence over asset overlap.
    /// An edge that is not needed, or would close a cycle, is left out.
    /// </summary>
    /// <returns>Whether any edge was added.</returns>
    public bool AddOverlapEdges()
    {
        var added = false;

        for (var node = 0; node < NodeCount; node++)
        {
            var plugin = _graph[node];
            var pluginAssetCount = plugin.AssetCount;

            if (plugin.OverrideRecordCount == 0 && pluginAssetCount == 0) continue;

            // Masters and non-masters are sorted separately now, so this should add nothing for
            // them, but it stays as a safety net.
            for (var other = node + 1; other < NodeCount; other++)
            {
                var otherPlugin = _graph[other];

                // Only direct edges are checked, not paths, for speed.
                if (_graph.FindEdge(node, other) is not null || _graph.FindEdge(other, node) is not null) continue;

                // Records matter more than assets: assets are only compared when the plugins do not
                // overlap in records, or override as many records as each other.
                bool outerLoadsFirst;
                EdgeType edgeType;

                if (plugin.OverrideRecordCount == otherPlugin.OverrideRecordCount || !plugin.DoRecordsOverlap(otherPlugin))
                {
                    var otherAssetCount = otherPlugin.AssetCount;
                    if (pluginAssetCount == otherAssetCount || !plugin.DoAssetsOverlap(otherPlugin)) continue;

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
}
