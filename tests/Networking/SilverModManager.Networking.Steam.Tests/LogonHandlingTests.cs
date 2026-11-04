using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Abstractions.Steam;
using SteamKit2;
using Xunit;

namespace SilverModManager.Networking.Steam.Tests;

public class LogonHandlingTests
{
    private static Session CreateSession() => new(NullLogger<Session>.Instance, Substitute.For<IAuthInterventionHandler>(), Substitute.For<IAuthStorage>());

    [Fact]
    public void ARejectedLogonIsStoredByTheTimeTheHandlerReturns()
    {
        var session = CreateSession();

        session.HandleLogon(EResult.AccessDenied, EResult.OK);

        session.LoginFailure.Should().NotBeNull();
        session.IsLoggedOn.Should().BeFalse();
    }

    [Fact]
    public void AnAcceptedLogonMarksTheSessionLoggedOn()
    {
        var session = CreateSession();

        session.HandleLogon(EResult.OK, EResult.OK);

        session.IsLoggedOn.Should().BeTrue();
        session.LoginFailure.Should().BeNull();
    }
}
