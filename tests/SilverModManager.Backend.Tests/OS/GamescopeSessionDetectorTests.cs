using FluentAssertions;
using NSubstitute;
using SilverModManager.Backend.OS;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Backend.Tests.OS;

public class GamescopeSessionDetectorTests
{
    private const int Own = 500;

    private readonly Dictionary<string, string> _environment = new();
    private readonly Dictionary<int, int> _parents = new();
    private readonly Dictionary<int, string[]> _arguments = new();
    private int _parentLookups;

    private GamescopeSessionDetector Create()
    {
        var env = Substitute.For<IEnvironmentReader>();
        env.Get(Arg.Any<string>()).Returns(call => _environment.GetValueOrDefault(call.Arg<string>()));

        var processes = Substitute.For<IProcessTracker>();
        processes.ParentOf(Arg.Any<int>()).Returns(call =>
        {
            _parentLookups++;
            return _parents.TryGetValue(call.Arg<int>(), out var parent) ? parent : (int?)null;
        });
        processes.ArgumentsOf(Arg.Any<int>()).Returns(call => _arguments.GetValueOrDefault(call.Arg<int>()) ?? []);

        return new GamescopeSessionDetector(env, processes, Own);
    }

    [Fact]
    public void EnvironmentNamingGamescopeIsGameMode()
    {
        _environment["XDG_CURRENT_DESKTOP"] = "gamescope";

        var detector = Create();

        detector.IsGameMode.Should().BeTrue();
        detector.Reason.Should().Contain("XDG_CURRENT_DESKTOP");
    }

    [Fact]
    public void AnAncestorSteamShWithSteampalIsGameMode()
    {
        _environment["XDG_CURRENT_DESKTOP"] = "KDE";
        _parents[500] = 400;
        _parents[400] = 300;
        _parents[300] = 1;
        _arguments[300] = ["/bin/bash", "/home/u/.steam/steam.sh", "-steampal"];

        var detector = Create();

        detector.IsGameMode.Should().BeTrue();
        detector.Reason.Should().Contain("steam.sh -steampal");
    }

    [Fact]
    public void SteamOsAndGamepadUiVariablesAreAThirdSignal()
    {
        _environment["SteamOS"] = "1";
        _environment["SteamGamepadUI"] = "1";

        Create().IsGameMode.Should().BeTrue();
    }

    [Fact]
    public void OneOfTheTwoSteamVariablesAloneIsNotGameMode()
    {
        _environment["SteamOS"] = "1";

        Create().IsGameMode.Should().BeFalse();
    }

    [Fact]
    public void ADesktopLaunchWithASteamShAncestorWithoutSteampalIsNot()
    {
        _parents[500] = 300;
        _parents[300] = 1;
        _arguments[300] = ["/bin/bash", "/home/u/.steam/steam.sh"];

        var detector = Create();

        detector.IsGameMode.Should().BeFalse();
        detector.Reason.Should().BeNull();
    }

    [Fact]
    public void TheWalkStopsAtInitAndOnARepeatedPid()
    {
        _parents[500] = 400;
        _parents[400] = 500;

        Create().IsGameMode.Should().BeFalse();
        _parentLookups.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public void TheWalkStopsAtInit()
    {
        _parents[500] = 1;
        _arguments[1] = ["/sbin/init", "steam.sh", "-steampal"];

        Create().IsGameMode.Should().BeFalse();
    }
}
