using SilverModManager.Sdk.Errors;

namespace SilverModManager.Networking.Forums.Auth;

/// <summary>
/// The user pressed Deny on the forum's consent page, or the forum answered the sign-in with an error.
/// </summary>
public sealed class SignInDeclinedException(string siteName) : UserFacingException($"Sign-in to {siteName} was declined")
{
    public override string Title => "Sign-in was declined";
}

/// <summary>
/// No sign-in came back before the wait ran out.
/// </summary>
public sealed class SignInTimedOutException(string siteName) : UserFacingException($"Sign-in to {siteName} timed out")
{
    public override string Title => "Sign-in timed out";
}
