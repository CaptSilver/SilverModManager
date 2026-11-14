using FluentAssertions;
using R3;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Abstractions.Accounts.Tests;

public class AccountButtonTests
{
    private sealed class FakeAccount : IModSourceAccount
    {
        public Subject<Unit> ChangedSubject { get; } = new();
        public string DisplayName => "Fake";
        public AccountStatus Status { get; set; } = new(AccountState.SignedOut, "");
        public Observable<Unit> Changed => ChangedSubject;
        public string? WhyNotAvailable { get; set; }
        public AccountAction? Action { get; set; }
    }

    private static AccountButton Button(FakeAccount account) => new(account);

    [Fact]
    public void ASignedInAccountNamesWhereTheCredentialIsKept()
    {
        var account = new FakeAccount
        {
            Status = new AccountStatus(AccountState.SignedIn, "the keyring"),
            Action = new AccountAction("Sign out", _ => Task.CompletedTask),
        };

        Button(account).Current.Should().Be(new ActionButtonState("Signed in. Stored in the keyring.", "Sign out"));
    }

    [Fact]
    public void ASignedInAccountWithoutAKnownSourceDoesNotInventOne()
    {
        var account = new FakeAccount
        {
            Status = new AccountStatus(AccountState.SignedIn, ""),
            Action = new AccountAction("Sign out", _ => Task.CompletedTask),
        };

        Button(account).Current.Text.Should().Be("Signed in.");
    }

    [Fact]
    public void ASignedOutAccountOffersItsAction()
    {
        var account = new FakeAccount { Action = new AccountAction("Add token", _ => Task.CompletedTask) };

        Button(account).Current.Should().Be(new ActionButtonState("Not signed in", "Add token"));
    }

    [Fact]
    public void AnExpiredAccountAsksToSignInAgain()
    {
        var account = new FakeAccount
        {
            Status = new AccountStatus(AccountState.Expired, ""),
            Action = new AccountAction("Sign in", _ => Task.CompletedTask),
        };

        Button(account).Current.Should().Be(new ActionButtonState("Session expired. Sign in again.", "Sign in"));
    }

    [Fact]
    public void AnUnavailableAccountShowsTheReasonAndNoButton()
    {
        var account = new FakeAccount
        {
            Status = new AccountStatus(AccountState.Unavailable, ""),
            WhyNotAvailable = "The keyring is locked.",
            // Even an action the account would offer is not shown: the row cannot do it right now.
            Action = new AccountAction("Sign in", _ => Task.CompletedTask),
        };

        Button(account).Current.Should().Be(new ActionButtonState("The keyring is locked.", null));
    }

    [Fact]
    public void ANotSetUpAccountShowsTheReasonAndNoButton()
    {
        var account = new FakeAccount
        {
            Status = new AccountStatus(AccountState.NotSetUp, ""),
            WhyNotAvailable = "Not set up.",
        };

        Button(account).Current.Should().Be(new ActionButtonState("Not set up.", null));
    }

    [Fact]
    public void TheStateStreamStartsWithTheCurrentRowAndFollowsChanges()
    {
        var account = new FakeAccount { Action = new AccountAction("Sign in", _ => Task.CompletedTask) };
        var seen = new List<ActionButtonState>();
        using var subscription = Button(account).State.Subscribe(seen.Add);

        account.Status = new AccountStatus(AccountState.SignedIn, "the keyring");
        account.Action = new AccountAction("Sign out", _ => Task.CompletedTask);
        account.ChangedSubject.OnNext(Unit.Default);

        seen.Select(s => s.Text).Should().Equal("Not signed in", "Signed in. Stored in the keyring.");
    }

    [Fact]
    public void ALaterSubscriberStartsFromTheStateOfThatMoment()
    {
        var account = new FakeAccount { Action = new AccountAction("Sign in", _ => Task.CompletedTask) };
        var button = Button(account);
        account.Status = new AccountStatus(AccountState.SignedIn, "");

        var seen = new List<ActionButtonState>();
        using var subscription = button.State.Subscribe(seen.Add);

        seen.Should().ContainSingle().Which.Text.Should().Be("Signed in.");
    }

    [Fact]
    public async Task InvokeRunsTheCurrentAction()
    {
        var runs = 0;
        var account = new FakeAccount { Action = new AccountAction("Sign in", _ => { runs++; return Task.CompletedTask; }) };

        await Button(account).Invoke(CancellationToken.None);

        runs.Should().Be(1);
    }

    [Fact]
    public async Task InvokeWithoutAnActionIsRefusedNotIgnored()
    {
        var account = new FakeAccount { Status = new AccountStatus(AccountState.Unavailable, "") };

        await Button(account).Invoking(b => b.Invoke(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
    }
}
