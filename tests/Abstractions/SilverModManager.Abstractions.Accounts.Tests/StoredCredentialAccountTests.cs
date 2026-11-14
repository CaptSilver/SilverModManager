using FluentAssertions;
using R3;

namespace SilverModManager.Abstractions.Accounts.Tests;

public class StoredCredentialAccountTests
{
    // Answers each reading with whatever the test last put in Next.
    private sealed class ScriptedAccount(Observable<Unit> refreshOn) : StoredCredentialAccount("Reading the stored login.", refreshOn)
    {
        public AccountReading Next { get; set; } = new(new AccountStatus(AccountState.SignedOut, ""));
        public int Readings { get; private set; }

        public override string DisplayName => "Scripted";

        public override AccountAction? Action => null;

        protected override ValueTask<AccountReading> ReadAsync(CancellationToken cancellationToken)
        {
            Readings++;
            return ValueTask.FromResult(Next);
        }
    }

    [Fact]
    public void BeforeItsFirstReadingTheAccountIsUnavailableNotSignedOut()
    {
        using var account = new ScriptedAccount(Observable.Never<Unit>());

        account.Status.Should().Be(new AccountStatus(AccountState.Unavailable, ""));
        account.WhyNotAvailable.Should().Be("Reading the stored login.");
        new AccountButton(account).Current.ButtonLabel.Should().BeNull();
    }

    [Fact]
    public async Task PrimingReadsTheStoreAndAnnouncesTheResult()
    {
        using var account = new ScriptedAccount(Observable.Never<Unit>());
        var announced = 0;
        using var _ = account.Changed.Subscribe(_ => announced++);

        await account.PrimeAsync(CancellationToken.None);

        account.Status.State.Should().Be(AccountState.SignedOut);
        announced.Should().Be(1);
    }

    [Fact]
    public async Task AReadingThatFindsNothingNewAnnouncesNothing()
    {
        using var account = new ScriptedAccount(Observable.Never<Unit>());
        await account.RefreshAsync(CancellationToken.None);
        var announced = 0;
        using var _ = account.Changed.Subscribe(_ => announced++);

        await account.RefreshAsync(CancellationToken.None);

        announced.Should().Be(0);
    }

    [Fact]
    public void AStoreChangeTriggersAFreshReading()
    {
        var storeChanged = new Subject<Unit>();
        using var account = new ScriptedAccount(storeChanged);
        account.Next = new AccountReading(new AccountStatus(AccountState.SignedIn, "private file"));

        // The fake reads synchronously, so the subscription has finished by the time OnNext returns.
        storeChanged.OnNext(Unit.Default);

        account.Readings.Should().Be(1);
        account.Status.Should().Be(new AccountStatus(AccountState.SignedIn, "private file"));
    }
}
