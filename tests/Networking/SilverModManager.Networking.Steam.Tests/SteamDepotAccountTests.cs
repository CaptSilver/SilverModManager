using FluentAssertions;
using NSubstitute;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Accounts;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Abstractions.Steam;
using R3;
using SilverModManager.Networking.Steam.DTOs;
using Xunit;

namespace SilverModManager.Networking.Steam.Tests;

/// <summary>The "Steam (depot files)" row of Settings → Accounts, over the stored Steam login.</summary>
public class SteamDepotAccountTests
{
    private readonly InMemorySecretStore _store = new();
    private readonly SecretStoreAuthStorage _storage;
    private readonly ISteamSession _session = Substitute.For<ISteamSession>();
    private readonly SteamDepotAccount _account;

    public SteamDepotAccountTests()
    {
        _storage = new SecretStoreAuthStorage(_store, NullLogger<SecretStoreAuthStorage>.Instance);
        _account = new SteamDepotAccount(_storage, _store, _session);
    }

    private Task StoreLogin() => _storage.SaveAsync(new AuthData { Username = "user", RefreshToken = "refresh" }.Save(), CancellationToken.None);

    [Fact]
    public async Task AStoredLoginReadsAsSignedInAndNamesWhereItIsKept()
    {
        await StoreLogin();
        await _account.RefreshAsync(CancellationToken.None);

        _account.Status.Should().Be(new AccountStatus(AccountState.SignedIn, "in-memory store"));
        _account.Action!.Label.Should().Be("Forget login");
    }

    [Fact]
    public async Task WithoutALoginTheRowIsSignedOutAndOffersTheQrSignIn()
    {
        await _account.RefreshAsync(CancellationToken.None);

        _account.Status.State.Should().Be(AccountState.SignedOut);
        _account.Action!.Label.Should().Be("Sign in with QR code");
        _account.DisplayName.Should().Be("Steam (depot files)");
    }

    [Fact]
    public async Task TheQrSignInConnectsTheSessionAndReadsTheStoredLoginAgain()
    {
        await _account.RefreshAsync(CancellationToken.None);
        _session.Connect(Arg.Any<CancellationToken>()).Returns(async _ => await StoreLogin());

        await _account.Action!.Run(CancellationToken.None);

        await _session.Received(1).Connect(Arg.Any<CancellationToken>());
        _account.Status.State.Should().Be(AccountState.SignedIn);
    }

    [Fact]
    public async Task ARefusedLoginIsReportedInWordsAndLeavesTheRowSignedOut()
    {
        await _account.RefreshAsync(CancellationToken.None);
        _session.Connect(Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new Exceptions.SteamLoginException("Steam refused the login"));

        await _account.Action!.Invoking(a => a.Run(CancellationToken.None)).Should().ThrowAsync<AccountActionRefusedException>().WithMessage("Steam refused the login");

        _account.Status.State.Should().Be(AccountState.SignedOut);
    }

    [Fact]
    public async Task AnUnavailableStoreNamesItsReasonAndIsNotSignedOut()
    {
        _store.Availability = (SecretUnavailableKind.Locked, "the wallet is locked");
        await _account.RefreshAsync(CancellationToken.None);

        _account.Status.State.Should().Be(AccountState.Unavailable);
        _account.WhyNotAvailable.Should().Be("The stored Steam login cannot be read: the wallet is locked");
        new AccountButton(_account).Current.ButtonLabel.Should().BeNull();
    }

    [Fact]
    public async Task ForgetLoginDeletesTheStoredData()
    {
        await StoreLogin();
        await _account.RefreshAsync(CancellationToken.None);

        await _account.Action!.Run(CancellationToken.None);

        (await _storage.TryLoadAsync(CancellationToken.None)).Should().BeOfType<AuthLoadResult.None>();
        _account.Status.State.Should().Be(AccountState.SignedOut);
    }

    [Fact]
    public async Task ForgetLoginAlsoEndsTheSessionsLogonSoTheNextSignInStartsANewOne()
    {
        // The session stays logged on after its token is deleted; without this the next "Sign in with QR code"
        // returned at once and the row stayed signed out.
        await StoreLogin();
        await _account.RefreshAsync(CancellationToken.None);

        await _account.Action!.Run(CancellationToken.None);

        _session.Received(1).LogOff();
    }

    [Fact]
    public async Task ForgetLoginRefusesInWordsWhenTheStoreWillNotTakeTheChange()
    {
        await StoreLogin();
        await _account.RefreshAsync(CancellationToken.None);
        _store.Availability = (SecretUnavailableKind.Locked, "the wallet is locked");

        await _account.Action!.Invoking(a => a.Run(CancellationToken.None)).Should().ThrowAsync<AccountActionRefusedException>();
    }

    [Fact]
    public async Task AChangeToTheStoredLoginAnnouncesItself()
    {
        await _account.RefreshAsync(CancellationToken.None);
        var seen = new List<AccountState>();
        using var subscription = _account.Changed.Subscribe(_ => seen.Add(_account.Status.State));

        await StoreLogin();
        await _account.RefreshAsync(CancellationToken.None);

        seen.Should().Contain(AccountState.SignedIn);
    }

    [Fact]
    public void TheBranchDefaultsToPublic()
    {
        new SteamDepotSettings().Branch.Should().Be("public");
    }
}
