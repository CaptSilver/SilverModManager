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

public class LutrisGameStarterTests
{
    private static readonly StoreClient Native = new(StoreClientKind.Lutris, "cyberpunk-2077", false);
    private static readonly StoreClient FromFlatpak = new(StoreClientKind.Lutris, "cyberpunk-2077", true);

    private readonly FakeClientRunner _runner = new();
    private readonly IExecutableLocator _exes = Substitute.For<IExecutableLocator>();
    private readonly LutrisFlatpakDependency _flatpak;
    private readonly IRunningGameDetector _detector = Substitute.For<IRunningGameDetector>();

    public LutrisGameStarterTests()
    {
        _flatpak = Substitute.For<LutrisFlatpakDependency>(_runner);
        _exes.Find("lutris").Returns(FileSystem.Shared.FromUnsanitizedFullPath("/usr/bin/lutris"));
        _flatpak.IsInstalled().Returns(false);
        _detector.WaitUntilGone(Arg.Any<GameInstallation>(), Arg.Any<GoneWait>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(RunningProcesses.Empty));
    }

    private LutrisGameStarter Starter() => new(_exes, _flatpak, _runner, _detector, NullLogger<LutrisGameStarter>.Instance);

    [Fact]
    public async Task Start_RunsRungameWithTheSlugForANativeLutris()
    {
        _detector.Running(Arg.Any<GameInstallation>(), Arg.Any<IReadOnlySet<int>?>()).Returns(ClientRequests.GameRunning);
        var request = ClientRequests.For(Native);

        var outcome = await Starter().Start(request, Substitute.For<IGameLaunch>(), CancellationToken.None);

        outcome.Should().Be(GameStartOutcome.Exited);
        var command = _runner.Commands.Should().ContainSingle().Subject;
        command.TargetFilePath.Should().Be("/usr/bin/lutris");
        command.Arguments.Should().Be("lutris:rungame/cyberpunk-2077");
    }

    [Fact]
    public async Task Start_UsesTheFlatpakWhenTheClientCameFromIt()
    {
        _flatpak.IsInstalled().Returns(true);
        _detector.Running(Arg.Any<GameInstallation>(), Arg.Any<IReadOnlySet<int>?>()).Returns(ClientRequests.GameRunning);

        await Starter().Start(ClientRequests.For(FromFlatpak), Substitute.For<IGameLaunch>(), CancellationToken.None);

        var command = _runner.Commands.Should().ContainSingle().Subject;
        command.TargetFilePath.Should().Be("flatpak");
        command.Arguments.Should().Be("run net.lutris.Lutris lutris:rungame/cyberpunk-2077");
    }

    [Fact]
    public async Task Start_FallsBackToTheFlatpakWhenOnlyItIsInstalled()
    {
        _exes.Find("lutris").Returns((AbsolutePath?)null);
        _flatpak.IsInstalled().Returns(true);
        _detector.Running(Arg.Any<GameInstallation>(), Arg.Any<IReadOnlySet<int>?>()).Returns(ClientRequests.GameRunning);

        await Starter().Start(ClientRequests.For(Native), Substitute.For<IGameLaunch>(), CancellationToken.None);

        _runner.Commands.Single().TargetFilePath.Should().Be("flatpak");
    }

    [Fact]
    public void CheckCanStart_RefusesWhenNoLutrisIsFound()
    {
        _exes.Find("lutris").Returns((AbsolutePath?)null);

        var act = () => Starter().CheckCanStart(ClientRequests.For(Native));

        act.Should().Throw<GameLaunchException>().Which.Message.Should().Contain("Lutris was not found").And.Contain("net.lutris.Lutris");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void CanCarryArguments_IsFalseWithAnyArgument(int argumentCount, bool expected)
    {
        var request = ClientRequests.For(Native, callerArgs: Enumerable.Repeat("-modded", argumentCount).ToArray());

        Starter().CanCarryArguments(request).Should().Be(expected);
    }

    [Fact]
    public async Task Start_WaitsForTheGameNotForLutrisToExit()
    {
        // Lutris stays open, so the command's own result must not end the wait: the runner is never awaited.
        var scans = 0;
        _detector.Running(Arg.Any<GameInstallation>(), Arg.Any<IReadOnlySet<int>?>())
            .Returns(_ => ++scans < 3 ? RunningProcesses.Empty : ClientRequests.GameRunning);
        var gone = new TaskCompletionSource<RunningProcesses>();
        _detector.WaitUntilGone(Arg.Any<GameInstallation>(), Arg.Any<GoneWait>(), Arg.Any<CancellationToken>()).Returns(gone.Task);
        var request = ClientRequests.For(Native, timing: ClientRequests.Timing(startTimeoutMs: 5_000));

        var start = Starter().Start(request, Substitute.For<IGameLaunch>(), CancellationToken.None);
        await Task.Delay(100);
        start.IsCompleted.Should().BeFalse("the game is running");
        gone.SetResult(RunningProcesses.Empty);

        (await start).Should().Be(GameStartOutcome.Exited);
        await _detector.Received(1).WaitUntilGone(Arg.Any<GameInstallation>(), Arg.Is<GoneWait>(w => w.Settle == request.Timing.Settle), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_TimesOutWhenNothingShowsUp()
    {
        _detector.Running(Arg.Any<GameInstallation>(), Arg.Any<IReadOnlySet<int>?>()).Returns(RunningProcesses.Empty);
        var request = ClientRequests.For(Native, timing: ClientRequests.Timing(startTimeoutMs: 40));
        var starter = Starter();

        var outcome = await starter.Start(request, Substitute.For<IGameLaunch>(), CancellationToken.None);

        outcome.Should().Be(GameStartOutcome.TimedOut);
        await _detector.DidNotReceive().WaitUntilGone(Arg.Any<GameInstallation>(), Arg.Any<GoneWait>(), Arg.Any<CancellationToken>());
        starter.AfterSessionMessage(request, outcome).Should()
            .Contain($"Lutris did not start {ClientRequests.GameName} within 1 second").And.Contain("press Play again");
    }

    [Theory]
    [InlineData(GameStartOutcome.Exited)]
    [InlineData(GameStartOutcome.Cancelled)]
    public void AfterSessionMessage_ForAnyOtherOutcome_IsNull(GameStartOutcome outcome)
        => Starter().AfterSessionMessage(ClientRequests.For(Native), outcome).Should().BeNull();
}
