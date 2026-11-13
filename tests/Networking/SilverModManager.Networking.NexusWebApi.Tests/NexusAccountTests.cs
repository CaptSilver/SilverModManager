using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;

namespace SilverModManager.Networking.NexusWebApi.Tests;

/// <summary>Which kind of Nexus Mods account the app is signed in with, read fresh from the login manager.</summary>
public class NexusAccountTests
{
    private static ILoginManager LoginAs(UserInfo? user)
    {
        var login = Substitute.For<ILoginManager>();
        login.GetUserInfoAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(user));
        return login;
    }

    private static UserInfo User(UserRole role) => new() { UserId = UserId.From(1), Name = "silver", UserRole = role, AvatarUrl = null };

    [Fact]
    public async Task Null_IsSignedOut() =>
        (await NexusAccount.Current(LoginAs(null), CancellationToken.None)).Should().Be(NexusAccountKind.SignedOut);

    [Fact]
    public async Task Premium_IsPremium() =>
        (await NexusAccount.Current(LoginAs(User(UserRole.Premium)), CancellationToken.None)).Should().Be(NexusAccountKind.Premium);

    [Theory]
    [InlineData(UserRole.Free)]
    [InlineData(UserRole.Supporter)]
    public async Task EveryOtherRole_IsFree(UserRole role) =>
        (await NexusAccount.Current(LoginAs(User(role)), CancellationToken.None)).Should().Be(NexusAccountKind.Free);

    [Fact]
    public async Task Throw_Propagates()
    {
        var login = Substitute.For<ILoginManager>();
        login.GetUserInfoAsync(Arg.Any<CancellationToken>()).Throws(new HttpRequestException("Connection refused"));

        var act = async () => await NexusAccount.Current(login, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
