using JetBrains.Annotations;

namespace SilverModManager.Networking.Thunderstore;

/// <summary>
/// Where a page gets a community's catalog from: the on-disk index cache, refreshed on demand.
/// </summary>
[PublicAPI]
public interface IThunderstoreCatalogSource
{
    /// <inheritdoc cref="ThunderstoreIndexCache.IsStale"/>
    bool IsStale(string community);

    /// <inheritdoc cref="ThunderstoreIndexCache.Refresh"/>
    Task Refresh(string community, CancellationToken cancellationToken);

    /// <inheritdoc cref="ThunderstoreIndexCache.Load"/>
    Task<ThunderstoreCatalog> Load(string community, CancellationToken cancellationToken);
}
