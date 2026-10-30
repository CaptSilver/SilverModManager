namespace SilverModManager.Networking.Forums;

/// <summary>
/// What the rest of the app may ask about a forum sign-in without starting one. Both answers are read
/// synchronously from state the secret store priming has already loaded, so a plan can be classified on any
/// thread.
/// </summary>
public interface IForumLoginState
{
    /// <summary>
    /// True once the site has an OAuth client id. While it is false the site cannot be signed in to at all, and a
    /// row for it should say so instead of offering a sign-in that cannot work.
    /// </summary>
    bool CanSignIn(ForumSite site);

    /// <summary>
    /// True when the app holds a token for the site that it can use: one that has not expired, or one it can
    /// refresh. An expired token with no refresh token reads as signed out.
    /// </summary>
    bool IsSignedIn(ForumSite site);
}
