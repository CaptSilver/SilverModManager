using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.TestFramework;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class UnfinishedLaunchEmitterTests
{
    private static readonly Guid SessionId = Guid.Parse("5b1c1a3e-7d0f-4a52-9a43-3c1b6d1c2f10");

    private readonly FakeGameLaunchSession _sessions = new();
    private readonly FakeLaunchFinishMarkers _markers = new();
    private readonly IRunningGameDetector _detector = Substitute.For<IRunningGameDetector>();
    private readonly IProcessTracker _tracker = Substitute.For<IProcessTracker>();
    private PendingLaunchFinishState _state = PendingLaunchFinishState.Pending;
    private bool _marked = true;

    public UnfinishedLaunchEmitterTests()
    {
        _markers.ReadWith = () => _marked
            ? new PendingLaunchFinish(LoadoutId.From(1), "Main", SessionId, ["GateA"], _state)
            : null;
        _detector.Running(Arg.Any<GameInstallation>(), Arg.Any<IReadOnlySet<int>?>()).Returns(RunningProcesses.Empty);
    }

    private static GameInstallation Installation(bool updatePending = false)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var folder = FileSystem.Shared.FromUnsanitizedFullPath("/tmp/test-game");
        var result = new GameLocatorResult
        {
            Game = game,
            Path = folder,
            Store = GameStore.Steam,
            StoreIdentifier = "42",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
            StoreUpdatePending = updatePending,
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, folder)));
    }

    private void Running(RunningProcesses processes) => _detector.Running(Arg.Any<GameInstallation>(), Arg.Any<IReadOnlySet<int>?>()).Returns(processes);

    private List<Diagnostic> Evaluate(GameInstallation? installation = null)
        => new UnfinishedLaunchEmitter(_sessions, _markers, _detector, _tracker).Evaluate(installation ?? Installation(), "Main").ToList();

    private static string Summary(Diagnostic d) => d.FormatSummary(new PlainDiagnosticWriter());

    [Fact]
    public void MarkerAndRunningAndAnActiveRegistration_EmitsNothing()
    {
        _sessions.Running = true;
        Running(new RunningProcesses([new TrackedProcess(777, "Game.exe")], []));

        Evaluate().Should().BeEmpty();
    }

    [Fact]
    public void MarkerAndRunningAndAForeignLockHolder_EmitsNothing()
    {
        _sessions.Described = new HeldByOtherProcess("Another SilverModManager has it.", 99);
        Running(new RunningProcesses([new TrackedProcess(777, "Game.exe")], []));

        Evaluate().Should().BeEmpty();
    }

    [Fact]
    public void MarkerAndARunningGame_EmitsAWarningWithTheStopAction()
    {
        Running(new RunningProcesses([new TrackedProcess(777, "Game.exe")], []));

        var diagnostic = Evaluate().Should().ContainSingle().Subject;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        Summary(diagnostic).Should().Be("Test Game is running (Game.exe, pid 777) and the changes from its last session are not in 'Main' yet. If you started it yourself, quit it and the app finishes on its own.");
        diagnostic.Actions.Should().Equal(DiagnosticActions.StopGameAndFinish("Test Game"));
    }

    [Fact]
    public void MarkerAndOnlyATool_EmitsASuggestionWithoutAnAction()
    {
        Running(new RunningProcesses([], [new TrackedProcess(600, "xEdit")]));

        var diagnostic = Evaluate().Should().ContainSingle().Subject;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        Summary(diagnostic).Should().Be("Close xEdit (600); it is open in Test Game's prefix, and the changes from the last session are taken into 'Main' once it is.");
        diagnostic.Actions.Should().BeEmpty();
    }

    [Fact]
    public void MarkerWhileSteamUpdates_EmitsASuggestionWithoutAnAction()
    {
        Running(new RunningProcesses([new TrackedProcess(777, "Game.exe")], []));

        var diagnostic = Evaluate(Installation(updatePending: true)).Should().ContainSingle().Subject;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        Summary(diagnostic).Should().Be("Steam is updating Test Game, so the changes from your last session are not in 'Main' yet. The app finishes this after the update, the next time it applies or launches.");
        diagnostic.Actions.Should().BeEmpty();
    }

    [Fact]
    public void NoMarker_EmitsNothing()
    {
        _marked = false;
        Running(new RunningProcesses([new TrackedProcess(777, "Game.exe")], []));

        Evaluate().Should().BeEmpty();
    }

    [Theory]
    [InlineData(PendingLaunchFinishState.AlreadySynced)]
    [InlineData(PendingLaunchFinishState.LoadoutGone)]
    public void AMarkerTheFinisherClears_EmitsNothing(PendingLaunchFinishState state)
    {
        _state = state;
        Running(new RunningProcesses([new TrackedProcess(777, "Game.exe")], []));

        Evaluate().Should().BeEmpty();
    }

    [Fact]
    public void MarkerAndNothingRunning_EmitsNothing() => Evaluate().Should().BeEmpty();

    [Fact]
    public void MarkerAndAnOutsideRunningGame_IsReportedLikeAnyRunningGame()
    {
        // Describe sees a game nobody here holds, which is the case the action exists for.
        _sessions.Described = new RunningOutsideApp("Test Game is running.", new RunningProcesses([new TrackedProcess(777, "Game.exe")], []));
        Running(new RunningProcesses([new TrackedProcess(777, "Game.exe")], []));

        Evaluate().Should().ContainSingle().Which.Actions.Should().HaveCount(1);
    }
}
