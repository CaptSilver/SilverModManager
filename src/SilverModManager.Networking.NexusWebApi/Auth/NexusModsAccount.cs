using R3;
using SilverModManager.Abstractions.Accounts;
using SilverModManager.Abstractions.NexusWebApi;

namespace SilverModManager.Networking.NexusWebApi.Auth;

/// <summary>
/// The Nexus Mods login as an account row. The login manager already works out which credential to use (OAuth or a
/// personal API key), so the row only reports whether a user is signed in and forwards the two buttons.
/// </summary>
public sealed class NexusModsAccount(ILoginManager login) : IModSourceAccount
{
    public string DisplayName => "Nexus Mods";

    // Where the credential is kept is not said here: `nexus-whoami` reports the origin of the key.
    public AccountStatus Status => new(login.UserInfo is null ? AccountState.SignedOut : AccountState.SignedIn, "");

    // The user info stream may replay its current value and fires again when only the name or role changed. The row
    // reads only signed in or out, so only a flip of that is announced.
    public Observable<Unit> Changed => Observable.Defer(() =>
    {
        var signedOut = login.UserInfo is null;
        return login.UserInfoObservable
            .Select(info => info is null)
            .Where(now =>
            {
                var flipped = now != signedOut;
                signedOut = now;
                return flipped;
            })
            .Select(_ => Unit.Default);
    });

    public string? WhyNotAvailable => null;

    public AccountAction? Action => login.UserInfo is null
        ? new AccountAction("Sign in", ct => login.LoginAsync(ct))
        : new AccountAction("Sign out", _ => login.Logout());
}
