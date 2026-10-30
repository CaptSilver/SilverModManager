using SilverModManager.Abstractions.Secrets;

namespace SilverModManager.Networking.Forums.Auth;

/// <summary>
/// One site's stored token, as the OAuth code sees it.
/// </summary>
public interface IForumTokenCache
{
    /// <summary>
    /// The token as of the last load or write, for callers that cannot await; null before the load has finished.
    /// </summary>
    OAuthToken? Current { get; }

    /// <summary>
    /// The token the cache holds, or null when there is none.
    /// </summary>
    ValueTask<OAuthToken?> GetTokenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stores the token a sign-in or refresh returned. When the store cannot take it the token is still used for
    /// the life of the process, and the result says so.
    /// </summary>
    ValueTask<SecretWriteResult> SetTokenAsync(OAuthToken token, CancellationToken cancellationToken);

    /// <summary>
    /// Forgets the token.
    /// </summary>
    ValueTask<SecretWriteResult> ClearAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads the token from the store again and returns what the cache holds afterwards. Another process may have
    /// refreshed it since this one looked.
    /// </summary>
    ValueTask<OAuthToken?> ReloadTokenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Finds the token cache of a site.
/// </summary>
public interface IForumCredentials
{
    IForumTokenCache For(ForumSite site);
}
