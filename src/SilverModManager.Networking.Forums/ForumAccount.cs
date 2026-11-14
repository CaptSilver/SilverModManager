using R3;
using SilverModManager.Abstractions.Accounts;

namespace SilverModManager.Networking.Forums;

/// <summary>One forum site as an account the Settings page can show.</summary>
public sealed class ForumAccount(IForumAccounts accounts, IForumSignInFlow flow, ForumSite site) : IModSourceAccount
{
    public string DisplayName => site.DisplayName();

    public AccountStatus Status
    {
        get
        {
            var status = accounts.StatusOf(site);
            return new AccountStatus(status.State switch
            {
                ForumAccountState.NotSetUp => AccountState.NotSetUp,
                ForumAccountState.SignedOut => AccountState.SignedOut,
                ForumAccountState.SignedIn => AccountState.SignedIn,
                ForumAccountState.Expired => AccountState.Expired,
                var other => throw new ArgumentOutOfRangeException(nameof(status), other, "Unknown forum account state"),
            }, status.Source);
        }
    }

    public Observable<Unit> Changed => accounts.Changed.Where(changed => changed == site).Select(_ => Unit.Default);

    public string? WhyNotAvailable => $"Sign-in for {site.DisplayName()} isn't set up yet. The site hasn't issued this app a client.";

    public AccountAction? Action => accounts.StatusOf(site).State switch
    {
        ForumAccountState.SignedOut or ForumAccountState.Expired => new AccountAction("Sign in", ct => flow.Run(site, ct)),
        ForumAccountState.SignedIn => new AccountAction("Sign out", ct => accounts.SignOut(site, ct)),
        _ => null,
    };
}
