using FluentAssertions;
using SteamKit2;
using Xunit;

namespace SilverModManager.Networking.Steam.Tests;

public class LogonRejectionTests
{
    [Fact]
    public void AcceptedLogonIsNotAFailure() => LogonRejection.For(EResult.OK).Should().BeNull();

    [Theory]
    [InlineData(EResult.AccessDenied)]
    [InlineData(EResult.Expired)]
    [InlineData(EResult.InvalidPassword)]
    [InlineData(EResult.Fail)]
    [InlineData(EResult.ServiceUnavailable)]
    public void EveryOtherResultIsAFailureThatNamesTheResult(EResult result)
        => LogonRejection.For(result)!.Message.Should().Contain(result.ToString());

    [Theory]
    [InlineData(EResult.AccessDenied)]
    [InlineData(EResult.Expired)]
    public void ARejectedTokenPointsAtLogout(EResult result)
        => LogonRejection.For(result)!.Message.Should().Contain("steam logout");

    [Fact]
    public void ATransientOutageDoesNotBlameTheStoredLogin()
        => LogonRejection.For(EResult.ServiceUnavailable)!.Message.Should().NotContain("steam logout");
}
