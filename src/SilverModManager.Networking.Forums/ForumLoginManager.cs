using SilverModManager.Networking.Forums.Auth;

namespace SilverModManager.Networking.Forums;

/// <summary>
/// Whether the user can and has signed in to each forum.
/// </summary>
public sealed class ForumLoginManager : IForumLoginState
{
    private readonly ForumOAuth _oauth;
    private readonly IForumCredentials _credentials;
    private readonly TimeProvider _time;

    public ForumLoginManager(ForumOAuth oauth, IForumCredentials credentials, TimeProvider time)
    {
        _oauth = oauth;
        _credentials = credentials;
        _time = time;
    }

    /// <inheritdoc />
    public bool CanSignIn(ForumSite site) => _oauth.HasClientId(site);

    /// <inheritdoc />
    public bool IsSignedIn(ForumSite site)
        => _credentials.For(site).Current is { } token && (token.CanRefresh || !token.HasExpired(_time.GetUtcNow()));
}
