using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Secrets;

namespace SilverModManager.Networking.Forums.Auth;

/// <summary>
/// The two forums' token caches, one per site over the same secret store.
/// </summary>
public sealed class ForumCredentials : IForumCredentials, ISecretPrimable, IDisposable
{
    private readonly Dictionary<ForumSite, ForumCredentialCache> _caches;

    public ForumCredentials(ISecretStore store, ILoggerFactory loggerFactory)
    {
        _caches = Enum.GetValues<ForumSite>().ToDictionary(
            site => site,
            site => new ForumCredentialCache(site, store, loggerFactory.CreateLogger<ForumCredentialCache>()));
    }

    /// <inheritdoc />
    public IForumTokenCache For(ForumSite site) => Cache(site);

    /// <summary>
    /// The site's cache, with the parts the Settings page reads (source, whether it is writable).
    /// </summary>
    public ForumCredentialCache Cache(ForumSite site) => _caches[site];

    /// <inheritdoc />
    public async ValueTask PrimeAsync(CancellationToken cancellationToken)
    {
        foreach (var cache in _caches.Values) await cache.PrimeAsync(cancellationToken);
    }

    public void Dispose()
    {
        foreach (var cache in _caches.Values) cache.Dispose();
    }
}
