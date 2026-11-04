using SilverModManager.Networking.Steam.Exceptions;
using SteamKit2;

namespace SilverModManager.Networking.Steam;

/// <summary>
/// Turns the result Steam sends back for a logon into the failure the caller should see.
/// </summary>
internal static class LogonRejection
{
    /// <summary>
    /// The failure for a rejected logon, or null when Steam accepted it.
    /// </summary>
    public static SteamLoginException? For(EResult result) => result switch
    {
        EResult.OK => null,

        // The token itself is no good; waiting or retrying cannot fix it.
        EResult.AccessDenied or EResult.Expired or EResult.InvalidPassword or EResult.InvalidLoginAuthCode
            or EResult.AccountLogonDenied or EResult.Revoked or EResult.LoggedInElsewhere
            => new SteamLoginException($"Steam rejected the stored login ({result}). Run `steam logout`, then sign in again."),

        EResult.ServiceUnavailable or EResult.Busy or EResult.Timeout or EResult.TryAnotherCM or EResult.NoConnection
            => new SteamLoginException($"Steam could not sign in right now ({result}). Try again in a moment."),

        _ => new SteamLoginException($"Steam rejected the sign-in ({result})."),
    };
}
