using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Abstractions.Games.Starters;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.Tests.Starters;

public class SteamGameStarterTests
{
    [Theory]
    [InlineData(300, "5 minutes")]
    [InlineData(60, "1 minute")]
    [InlineData(120, "2 minutes")]
    [InlineData(90, "90 seconds")]
    [InlineData(30, "30 seconds")]
    [InlineData(1, "1 second")]
    public void DescribeTimeout_IsBuiltFromTheTimeSpan(int seconds, string expected)
    {
        SteamGameStarter.DescribeTimeout(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }

    [Fact]
    public void DescribeTimeout_RoundsAShortTimeoutUpToASecond()
    {
        SteamGameStarter.DescribeTimeout(TimeSpan.FromMilliseconds(80)).Should().Be("1 second");
    }

    private static readonly StoreClient SteamClient = new(StoreClientKind.Steam, "1091500", false);

    private static (SteamGameStarter Starter, GameStartRequest Request) Rig(StartTiming timing)
    {
        var tracker = Substitute.For<IProcessTracker>();
        tracker.IsSteamRunning().Returns(false);
        var starter = new SteamGameStarter(Substitute.For<ISteamUriLauncher>(), tracker, Substitute.For<IRunningGameDetector>(), NullLogger<SteamGameStarter>.Instance);
        return (starter, StarterRequests.For(SteamClient, timing));
    }

    [Fact]
    public async Task AfterSessionMessage_ForATimeout_NamesTheGameAndTheLimit()
    {
        var timing = new StartTiming(TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(5), TimeSpan.Zero, TimeSpan.Zero, TimeProvider.System);
        var (starter, request) = Rig(timing);
        var launch = Substitute.For<IGameLaunch>();

        var outcome = await starter.Start(request, launch, CancellationToken.None);

        outcome.Should().Be(GameStartOutcome.TimedOut);
        starter.AfterSessionMessage(request, outcome).Should()
            .Contain($"Steam did not start {StarterRequests.GameName} within 1 second").And.Contain("press Play again");
    }

    [Theory]
    [InlineData(GameStartOutcome.Exited)]
    [InlineData(GameStartOutcome.Cancelled)]
    public void AfterSessionMessage_ForAnyOtherOutcome_IsNull(GameStartOutcome outcome)
    {
        var (starter, request) = Rig(StarterRequests.ZeroTiming);

        starter.AfterSessionMessage(request, outcome).Should().BeNull();
    }

    [Fact]
    public void CanCarryArguments_IsTrue_BecauseTheLaunchOptionAppliesThem()
    {
        var (starter, request) = Rig(StarterRequests.ZeroTiming);

        starter.CanCarryArguments(request).Should().BeTrue();
    }
}
