using R3;
using SilverModManager.Networking.Forums.Auth;

namespace SilverModManager.Networking.Forums;

/// <summary>
/// The forum accounts as the pages show them, over the login manager and the token caches it works with.
/// </summary>
public sealed class ForumAccounts : IForumAccounts
{
    private readonly ForumLoginManager _login;
    private readonly ForumCredentials _credentials;
    private readonly TimeProvider _time;

    public ForumAccounts(ForumLoginManager login, ForumCredentials credentials, TimeProvider time)
    {
        _login = login;
        _credentials = credentials;
        _time = time;
        Changed = Observable.Merge(Enum.GetValues<ForumSite>().Select(Announce));
    }

    /// <inheritdoc />
    public Observable<ForumSite> Changed { get; }

    /// <inheritdoc />
    public ForumAccountStatus StatusOf(ForumSite site)
    {
        if (!_login.CanSignIn(site)) return new ForumAccountStatus(ForumAccountState.NotSetUp, "");

        var cache = _credentials.Cache(site);
        if (cache.Current is not { } token) return new ForumAccountStatus(ForumAccountState.SignedOut, "");

        var state = _login.IsSignedIn(site) ? ForumAccountState.SignedIn : ForumAccountState.Expired;
        return new ForumAccountStatus(state, cache.Source);
    }

    /// <inheritdoc />
    public Task SignIn(ForumSite site, CancellationToken cancellationToken) => _login.SignIn(site, cancellationToken);

    /// <inheritdoc />
    public Task SignOut(ForumSite site, CancellationToken cancellationToken) => _login.SignOut(site, cancellationToken);

    // Every write announces, but a refresh swapping one usable token for another changes nothing a page shows, and
    // would make the modlist page check its whole list again. So only a change of what the account reads as counts:
    // signed out, signed in, expired, or where the token is kept.
    private Observable<ForumSite> Announce(ForumSite site) => Observable.Defer(() =>
    {
        var last = Reading(site);
        return _credentials.Cache(site).Changes
            .Where(_ =>
            {
                var now = Reading(site);
                var changed = now != last;
                last = now;
                return changed;
            })
            .Select(_ => site);
    });

    // Read from the cache, not StatusOf: a site without a client id still has a token that can come and go.
    private (bool Held, bool SignedIn, string Source) Reading(ForumSite site)
    {
        var cache = _credentials.Cache(site);
        return (cache.Current is not null, _login.IsSignedIn(site), cache.Source);
    }
}
