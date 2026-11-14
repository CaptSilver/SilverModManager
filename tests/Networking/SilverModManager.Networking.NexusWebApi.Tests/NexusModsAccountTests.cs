using FluentAssertions;
using NSubstitute;
using R3;
using SilverModManager.Abstractions.Accounts;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Networking.NexusWebApi.Auth;

namespace SilverModManager.Networking.NexusWebApi.Tests;

/// <summary>The Nexus Mods row of Settings → Accounts, over the login manager.</summary>
public class NexusModsAccountTests
{
    private static UserInfo User() => new() { UserId = UserId.From(1), Name = "silver", UserRole = UserRole.Premium, AvatarUrl = null };

    private static (ILoginManager Login, BehaviorSubject<UserInfo?> Info) Login(UserInfo? user)
    {
        var info = new BehaviorSubject<UserInfo?>(user);
        var login = Substitute.For<ILoginManager>();
        login.UserInfoObservable.Returns(info);
        login.UserInfo.Returns(_ => info.Value);
        return (login, info);
    }

    [Fact]
    public void ASignedInUserReadsAsSignedInAndOffersSignOut()
    {
        var (login, _) = Login(User());
        var account = new NexusModsAccount(login);

        account.Status.State.Should().Be(AccountState.SignedIn);
        account.Action!.Label.Should().Be("Sign out");
    }

    [Fact]
    public void WithoutAUserTheRowOffersSignIn()
    {
        var (login, _) = Login(null);
        var account = new NexusModsAccount(login);

        account.Status.State.Should().Be(AccountState.SignedOut);
        account.Action!.Label.Should().Be("Sign in");
        account.DisplayName.Should().Be("Nexus Mods");
    }

    [Fact]
    public async Task SigningInRunsTheLoginManagersSignIn()
    {
        var (login, _) = Login(null);

        await new NexusModsAccount(login).Action!.Run(CancellationToken.None);

        await login.Received(1).LoginAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SigningOutCallsLogout()
    {
        var (login, _) = Login(User());

        await new NexusModsAccount(login).Action!.Run(CancellationToken.None);

        await login.Received(1).Logout();
    }

    [Fact]
    public void TheRowFollowsUserInfoChanges()
    {
        var (login, info) = Login(null);
        var button = new AccountButton(new NexusModsAccount(login));
        var seen = new List<string>();
        using var subscription = button.State.Subscribe(state => seen.Add(state.Text));

        info.OnNext(User());

        seen.Should().Equal("Not signed in", "Signed in.");
    }

    [Fact]
    public void AChangeOfNameOrRoleDoesNotAnnounce()
    {
        var (login, info) = Login(User());
        var announced = 0;
        using var subscription = new NexusModsAccount(login).Changed.Subscribe(_ => announced++);

        info.OnNext(User() with { Name = "renamed" });

        announced.Should().Be(0);
    }

    [Fact]
    public void TheSsoSlugIsEmptyByDefault()
    {
        new NexusAuthSettings().SsoApplicationSlug.Should().BeEmpty();
    }
}
