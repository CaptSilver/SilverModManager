namespace SilverModManager.Networking.NexusWebApi;

internal sealed class NexusCollectionsBrowser(IGraphQlClient graphQl) : INexusCollectionsBrowser
{
    public async Task<NexusCollectionsSearchPage> Search(NexusCollectionsSearchQuery query, CancellationToken cancellationToken)
    {
        var result = await graphQl.QueryCollections(NexusCollectionsSearchInputs.Filter(query), NexusCollectionsSearchInputs.Sort(query), query.Offset, query.Count, cancellationToken);
        if (!result.TryGetData(out var collections))
            throw new InvalidOperationException($"Nexus Mods returned no collections for game {query.Game}: {string.Join("; ", result.Errors.Select(e => e.Value.Message))}");

        return new NexusCollectionsSearchPage(collections.Nodes.Select(NexusCollectionSearchHit.From).ToArray(), query.Offset, collections.TotalCount);
    }
}
