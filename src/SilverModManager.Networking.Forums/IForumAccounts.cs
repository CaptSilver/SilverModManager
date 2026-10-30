using R3;

namespace SilverModManager.Networking.Forums;

public enum ForumAccountState
{
    /// <summary>The site has no OAuth client id, so there is nothing to sign in to.</summary>
    NotSetUp,

    /// <summary>The app can sign in to the site and holds no usable token.</summary>
    SignedOut,

    /// <summary>The app holds a token it can use or refresh.</summary>
    SignedIn,

    /// <summary>A token is stored but has lapsed and cannot be refreshed.</summary>
    Expired,
}

/// <param name="Source">Where the token is kept, in words for the user; empty without one.</param>
public readonly record struct ForumAccountStatus(ForumAccountState State, string Source);

/// <summary>
/// What the Settings page and the modlist page need of a forum account: its status, when that changes, and the
/// two actions.
/// </summary>
public interface IForumAccounts
{
    ForumAccountStatus StatusOf(ForumSite site);

    /// <summary>
    /// Raised with the site whenever its token appears or goes. Does not replay the current state.
    /// </summary>
    Observable<ForumSite> Changed { get; }

    /// <inheritdoc cref="ForumLoginManager.SignIn"/>
    Task SignIn(ForumSite site, CancellationToken cancellationToken);

    Task SignOut(ForumSite site, CancellationToken cancellationToken);
}
