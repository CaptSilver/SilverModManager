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

        var usable = token.CanRefresh || !token.HasExpired(_time.GetUtcNow());
        return new ForumAccountStatus(usable ? ForumAccountState.SignedIn : ForumAccountState.Expired, cache.Source);
    }

    /// <inheritdoc />
    public Task SignIn(ForumSite site, CancellationToken cancellationToken) => _login.SignIn(site, cancellationToken);

    /// <inheritdoc />
    public Task SignOut(ForumSite site, CancellationToken cancellationToken) => _login.SignOut(site, cancellationToken);

    // HasCredential replays its current value, which is not a change.
    private Observable<ForumSite> Announce(ForumSite site)
        => _credentials.Cache(site).HasCredential.Skip(1).DistinctUntilChanged().Select(_ => site);
}
