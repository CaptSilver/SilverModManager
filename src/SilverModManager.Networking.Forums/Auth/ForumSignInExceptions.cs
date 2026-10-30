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

/// <summary>
/// The site has no OAuth client id yet, so nothing is sent to it. The feature stays dark until an admin issues one.
/// </summary>
public sealed class SignInNotSetUpException(string siteName) : UserFacingException($"Sign-in for {siteName} isn't set up yet")
{
    public override string Title => "Sign-in isn't available";
}

/// <summary>
/// The forum answered the sign-in with a refusal or with a reply that holds no access token.
/// </summary>
public sealed class SignInRejectedException(string siteName) : UserFacingException($"{siteName} refused the sign-in")
{
    public override string Title => "Sign-in was refused";
}

/// <summary>
/// There is no stored token for the site, so the request cannot be made yet.
/// </summary>
public sealed class NotSignedInException(string siteName) : UserFacingException($"You're not signed in to {siteName}")
{
    public override string Title => "Not signed in";
}

/// <summary>
/// The stored token has lapsed and cannot be renewed, or the forum refused the renewal.
/// </summary>
public sealed class SignInAgainException(string siteName) : UserFacingException($"Your {siteName} session expired. Sign in again, then download again.")
{
    public override string Title => "Sign in again";
}
