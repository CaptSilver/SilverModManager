namespace SilverModManager.Networking.Forums;

/// <summary>
/// Starts a forum sign-in from a page and tells the user how it ended. A sign-in the user declined or let time out
/// is a toast, not an error; anything else is reported as a failure.
/// </summary>
public interface IForumSignInFlow
{
    Task Run(ForumSite site, CancellationToken cancellationToken);
}
