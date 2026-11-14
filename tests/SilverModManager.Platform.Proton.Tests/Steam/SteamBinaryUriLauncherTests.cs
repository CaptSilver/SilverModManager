using CliWrap;
using FluentAssertions;
using NSubstitute;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk;

namespace SilverModManager.Platform.Proton.Tests.Steam;

public class SteamBinaryUriLauncherTests
{
    private readonly IProcessRunner _runner = Substitute.For<IProcessRunner>();
    private readonly IOSInterop _interop = Substitute.For<IOSInterop>();
    private readonly IEnvironmentReader _environment = Substitute.For<IEnvironmentReader>();

    private SteamBinaryUriLauncher Create(params string[] existing)
    {
        return new SteamBinaryUriLauncher(_runner, _interop, _environment, existing.Contains);
    }

    [Fact]
    public void RunsTheSteamBinaryFromPathWithTheUrl()
    {
        _environment.Get("PATH").Returns("/nope:/usr/bin");
        Command? started = null;
        _runner.When(r => r.Run(Arg.Any<Command>(), Arg.Any<bool>())).Do(call => started = call.Arg<Command>());

        Create("/usr/bin/steam").Open(new Uri("steam://run/1091500"));

        started.Should().NotBeNull();
        started!.TargetFilePath.Should().Be("/usr/bin/steam");
        started.Arguments.Should().Be("steam://run/1091500");
        _interop.DidNotReceive().OpenUri(Arg.Any<Uri>());
    }

    [Fact]
    public void FallsBackToTheOsInteropWhenNoSteamBinaryIsOnPath()
    {
        _environment.Get("PATH").Returns("/nope:/usr/bin");
        var uri = new Uri("steam://run/1091500");

        Create().Open(uri);

        _interop.Received(1).OpenUri(uri);
        _runner.DidNotReceive().Run(Arg.Any<Command>(), Arg.Any<bool>());
    }

    [Fact]
    public void FallsBackToTheOsInteropWhenPathIsUnset()
    {
        var uri = new Uri("steam://exit");

        Create("/usr/bin/steam").Open(uri);

        _interop.Received(1).OpenUri(uri);
    }

    [Fact]
    public void RefusesANonSteamScheme()
    {
        _environment.Get("PATH").Returns("/usr/bin");

        var act = () => Create("/usr/bin/steam").Open(new Uri("https://example.com"));

        act.Should().Throw<ArgumentException>();
        _runner.DidNotReceive().Run(Arg.Any<Command>(), Arg.Any<bool>());
    }
}
