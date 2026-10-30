using R3;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.Forums;

/// <summary>
/// The Settings row of one forum account: what its status says and the button that signs in or out.
/// </summary>
public sealed class ForumAccountButton : IActionButtonSource
{
    private readonly IForumAccounts _accounts;
    private readonly IForumSignInFlow _flow;
    private readonly ForumSite _site;

    public ForumAccountButton(IForumAccounts accounts, IForumSignInFlow flow, ForumSite site)
    {
        _accounts = accounts;
        _flow = flow;
        _site = site;
        // Deferred so a subscriber that comes later starts from the state of that moment.
        State = Observable.Defer(() => accounts.Changed.Where(changed => changed == site).Select(_ => Current).Prepend(Current));
    }

    /// <inheritdoc />
    public Observable<ActionButtonState> State { get; }

    /// <inheritdoc />
    public ActionButtonState Current
    {
        get
        {
            var status = _accounts.StatusOf(_site);
            return status.State switch
            {
                ForumAccountState.NotSetUp => new ActionButtonState($"Sign-in for {_site.DisplayName()} isn't set up yet. The site hasn't issued this app a client.", null),
                ForumAccountState.SignedOut => new ActionButtonState("Not signed in", "Sign in"),
                ForumAccountState.SignedIn => new ActionButtonState(status.Source == "" ? "Signed in." : $"Signed in. Stored in {status.Source}.", "Sign out"),
                ForumAccountState.Expired => new ActionButtonState("Session expired. Sign in again.", "Sign in"),
                _ => throw new ArgumentOutOfRangeException(nameof(status), status.State, "Unknown account state"),
            };
        }
    }

    /// <inheritdoc />
    public Task Invoke(CancellationToken cancellationToken) => _accounts.StatusOf(_site).State switch
    {
        ForumAccountState.SignedOut or ForumAccountState.Expired => _flow.Run(_site, cancellationToken),
        ForumAccountState.SignedIn => _accounts.SignOut(_site, cancellationToken),
        var other => throw new InvalidOperationException($"The {_site.DisplayName()} row offers no action while it is {other}"),
    };
}
