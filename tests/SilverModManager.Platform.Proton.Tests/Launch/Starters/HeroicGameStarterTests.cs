using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Starters;
using SilverModManager.Platform.Proton.Launch.Starters;
using SilverModManager.Platform.Proton.ToolRunner.Dependencies;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests.Launch.Starters;

public class HeroicGameStarterTests : IDisposable
{
    private static readonly StoreClient Native = new(StoreClientKind.Heroic, "Sugar", false);
    private static readonly StoreClient FromFlatpak = new(StoreClientKind.Heroic, "Sugar", true);

    private readonly string _stateHome = Path.Combine(Path.GetTempPath(), "smm-heroic-state-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClientRunner _runner = new();
    private readonly IExecutableLocator _exes = Substitute.For<IExecutableLocator>();
    private readonly HeroicFlatpakDependency _flatpak;
    private readonly IHeroicVersionProbe _versions = Substitute.For<IHeroicVersionProbe>();
    private readonly IRunningGameDetector _detector = Substitute.For<IRunningGameDetector>();
    private readonly IOSInterop _os = Substitute.For<IOSInterop>();

    public HeroicGameStarterTests()
    {
        _flatpak = Substitute.For<HeroicFlatpakDependency>(_runner);
        _exes.Find("heroic").Returns(FileSystem.Shared.FromUnsanitizedFullPath("/usr/bin/heroic"));
        _flatpak.IsInstalled().Returns(false);
        _detector.WaitUntilGone(Arg.Any<GameInstallation>(), Arg.Any<GoneWait>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(RunningProcesses.Empty));
        _detector.Running(Arg.Any<GameInstallation>(), Arg.Any<IReadOnlySet<int>?>()).Returns(ClientRequests.GameRunning);
    }

    public void Dispose()
    {
        if (Directory.Exists(_stateHome)) Directory.Delete(_stateHome, recursive: true);
    }

    private HeroicGameStarter Starter() => new(_exes, _flatpak, _versions, _runner, _detector, _os, FileSystem.Shared, NullLogger<HeroicGameStarter>.Instance, () => _stateHome);

    private static readonly Version V216 = new(2, 16, 0);
    private static readonly Version V219 = new(2, 19, 0);

    [Fact]
    public void LaunchUrl_EncodesRepeatedArgsAt219()
    {
        var url = HeroicGameStarter.LaunchUrl("Sugar", "legendary", ["-modded", "-name=John Doe"], V219);

        url!.OriginalString.Should().Be("heroic://launch?appName=Sugar&runner=legendary&arg=-modded&arg=-name%3DJohn%20Doe&gui=false");
    }

    [Fact]
    public void LaunchUrl_SendsRawArgsAt216()
    {
        var url = HeroicGameStarter.LaunchUrl("Sugar", "gog", ["-modded", "-skipStartScreen"], new Version(2, 17, 1));

        url!.OriginalString.Should().Be("heroic://launch?appName=Sugar&runner=gog&arg=-modded&arg=-skipStartScreen&gui=false");
    }

    [Fact]
    public void LaunchUrl_WithAnUnknownVersion_UsesTheEncodedForm()
        => HeroicGameStarter.LaunchUrl("Sugar", "nile", ["a b"], null)!.AbsoluteUri.Should().Contain("arg=a%20b");

    [Fact]
    public void LaunchUrl_WithoutArgumentsBelow216_UsesThePathForm()
        => HeroicGameStarter.LaunchUrl("Sugar", "legendary", [], new Version(2, 15, 2))!.AbsoluteUri.Should().Be("heroic://launch/legendary/Sugar");

    [Fact]
    public void LaunchUrl_WithArgumentsBelow216_IsNotCallable()
        => HeroicGameStarter.LaunchUrl("Sugar", "legendary", ["-modded"], new Version(2, 15, 2)).Should().BeNull();

    [Fact]
    public void CanCarryArguments_IsFalseForASpaceAt216AndTrueAt219()
    {
        var request = ClientRequests.For(Native, callerArgs: ["-name=John Doe"]);

        _versions.Read(false).Returns(V216);
        Starter().CanCarryArguments(request).Should().BeFalse();

        _versions.Read(false).Returns(V219);
        Starter().CanCarryArguments(request).Should().BeTrue();
    }

    [Fact]
    public void CanCarryArguments_IsFalseBelow216ButTrueWithoutArguments()
    {
        _versions.Read(false).Returns(new Version(2, 15, 2));

        Starter().CanCarryArguments(ClientRequests.For(Native, callerArgs: ["-modded"])).Should().BeFalse();
        Starter().CanCarryArguments(ClientRequests.For(Native)).Should().BeTrue();
    }

    [Fact]
    public void CanCarryArguments_AnUnknownVersionCountsAsNewest()
    {
        _versions.Read(false).Returns((Version?)null);

        Starter().CanCarryArguments(ClientRequests.For(Native, callerArgs: ["-name=John Doe"], extraArgs: ["-x"])).Should().BeTrue();
    }

    [Fact]
    public void CanCarryArguments_AsksTheProbeAboutTheFlatpakForAFlatpakClient()
    {
        _versions.Read(true).Returns(new Version(2, 15, 0));

        Starter().CanCarryArguments(ClientRequests.For(FromFlatpak, callerArgs: ["-modded"])).Should().BeFalse();
    }

    [Theory]
    [InlineData("Epic Games Store", "legendary")]
    [InlineData("GOG", "gog")]
    [InlineData("Amazon", "nile")]
    public async Task Start_UsesTheRunnerOfTheStore(string store, string runner)
    {
        await Starter().Start(ClientRequests.For(Native, GameStore.From(store)), Substitute.For<IGameLaunch>(), CancellationToken.None);

        var command = _runner.Commands.Should().ContainSingle().Subject;
        command.TargetFilePath.Should().Be("/usr/bin/heroic");
        command.Arguments.Should().Be($"--no-gui heroic://launch?appName=Sugar&runner={runner}&gui=false");
    }

    [Fact]
    public async Task Start_PrefersTheFlatpakOnlyWhenTheProbePasses()
    {
        _flatpak.IsInstalled().Returns(true);
        await Starter().Start(ClientRequests.For(FromFlatpak), Substitute.For<IGameLaunch>(), CancellationToken.None);
        _runner.Commands.Last().TargetFilePath.Should().Be("flatpak");
        _runner.Commands.Last().Arguments.Should().StartWith("run com.heroicgameslauncher.hgl --no-gui heroic://launch?");

        _flatpak.IsInstalled().Returns(false);
        await Starter().Start(ClientRequests.For(FromFlatpak), Substitute.For<IGameLaunch>(), CancellationToken.None);
        _runner.Commands.Last().TargetFilePath.Should().Be("/usr/bin/heroic");
    }

    [Fact]
    public async Task Start_FallsBackToOpenUriWithoutAnyBinary()
    {
        _exes.Find("heroic").Returns((AbsolutePath?)null);

        var outcome = await Starter().Start(ClientRequests.For(Native), Substitute.For<IGameLaunch>(), CancellationToken.None);

        outcome.Should().Be(GameStartOutcome.Exited);
        _runner.Commands.Should().BeEmpty();
        _os.Received(1).OpenUri(Arg.Is<Uri>(u => u.AbsoluteUri.StartsWith("heroic://launch/")));
    }

    [Fact]
    public async Task Start_AChildThatExitsAtOnceStillWaitsForTheGame()
    {
        // A running Heroic took the URL and our child left; the game is what the wait is for.
        var scans = 0;
        _detector.Running(Arg.Any<GameInstallation>(), Arg.Any<IReadOnlySet<int>?>())
            .Returns(_ => ++scans < 4 ? RunningProcesses.Empty : ClientRequests.GameRunning);
        var request = ClientRequests.For(Native, timing: ClientRequests.Timing(startTimeoutMs: 5_000));

        var outcome = await Starter().Start(request, Substitute.For<IGameLaunch>(), CancellationToken.None);

        outcome.Should().Be(GameStartOutcome.Exited);
        scans.Should().BeGreaterThanOrEqualTo(4);
        await _detector.Received(1).WaitUntilGone(Arg.Any<GameInstallation>(), Arg.Is<GoneWait>(w => w.Settle == request.Timing.Settle), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_RestartsTheClockWhileTheChildLives()
    {
        var childLives = true;
        _runner.OnRunAsync = async _ =>
        {
            await Task.Delay(250);
            childLives = false;
            return FakeClientRunner.Result(0);
        };
        _detector.Running(Arg.Any<GameInstallation>(), Arg.Any<IReadOnlySet<int>?>())
            .Returns(_ => childLives ? RunningProcesses.Empty : ClientRequests.GameRunning);
        var request = ClientRequests.For(Native, timing: ClientRequests.Timing(startTimeoutMs: 60));

        var outcome = await Starter().Start(request, Substitute.For<IGameLaunch>(), CancellationToken.None);

        outcome.Should().Be(GameStartOutcome.Exited, "Heroic was still preparing the launch");
    }

    [Fact]
    public async Task Start_RestartsTheClockWhileTheLaunchLogIsWritten()
    {
        var log = HeroicLaunchLog.PathFor(FileSystem.Shared, Native, "gog", _stateHome);
        Directory.CreateDirectory(log.Parent.ToString());
        File.WriteAllText(log.ToString(), "start\n");
        // Heroic writes a line between every two scans, for far longer than the start timeout, then the game shows.
        var scans = 0;
        _detector.Running(Arg.Any<GameInstallation>(), Arg.Any<IReadOnlySet<int>?>())
            .Returns(_ =>
            {
                if (++scans > 60) return ClientRequests.GameRunning;
                File.SetLastWriteTimeUtc(log.ToString(), DateTime.UtcNow.AddSeconds(scans));
                return RunningProcesses.Empty;
            });
        var request = ClientRequests.For(Native, timing: ClientRequests.Timing(startTimeoutMs: 80));
        var started = Stopwatch.StartNew();

        var outcome = await Starter().Start(request, Substitute.For<IGameLaunch>(), CancellationToken.None);

        started.Elapsed.Should().BeGreaterThan(request.Timing.StartTimeout, "the log kept the launch alive past the timeout");
        outcome.Should().Be(GameStartOutcome.Exited, "Heroic was still writing the game's launch log");
    }

    [Fact]
    public async Task Start_TimesOutWhenNothingShowsUpAndNothingIsHappening()
    {
        _detector.Running(Arg.Any<GameInstallation>(), Arg.Any<IReadOnlySet<int>?>()).Returns(RunningProcesses.Empty);
        var request = ClientRequests.For(Native, timing: ClientRequests.Timing(startTimeoutMs: 40));
        var starter = Starter();

        var outcome = await starter.Start(request, Substitute.For<IGameLaunch>(), CancellationToken.None);

        outcome.Should().Be(GameStartOutcome.TimedOut);
        starter.AfterSessionMessage(request, outcome).Should()
            .Contain($"Heroic did not start {ClientRequests.GameName} within 1 second").And.Contain("signed in").And.Contain("press Play again");
    }

    [Theory]
    [InlineData(GameStartOutcome.Exited)]
    [InlineData(GameStartOutcome.Cancelled)]
    public void AfterSessionMessage_ForAnyOtherOutcome_IsNull(GameStartOutcome outcome)
        => Starter().AfterSessionMessage(ClientRequests.For(Native), outcome).Should().BeNull();
}
