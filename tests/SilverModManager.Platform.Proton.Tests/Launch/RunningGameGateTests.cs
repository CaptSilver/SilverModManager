using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class RunningGameGateTests
{
    private readonly IProcessTracker _tracker = Substitute.For<IProcessTracker>();

    private static GameInstallation Installation()
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var folder = FileSystem.Shared.FromUnsanitizedFullPath("/tmp/silvermod-gate-game");
        var result = new GameLocatorResult
        {
            Game = game,
            Path = folder,
            Store = GameStore.Steam,
            StoreIdentifier = "42",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, folder)));
    }

    private LaunchContext Context(IReadOnlySet<int>? protectedPids = null)
        => new(Installation(), LoadoutId.From(1), "My Loadout", [], ProtectedPids: protectedPids);

    private RunningGameGate GateOver(Func<int, int?>? parentOf = null, IStartedTools? tools = null)
        => new(TestDetectors.Over(_tracker, parentOf: parentOf, tools: tools));

    [Fact]
    public void Check_LetsALaunchThroughWhenNothingRuns()
    {
        _tracker.FindGameProcesses(Arg.Any<GameInstallation>()).Returns([]);

        GateOver().Check(Context()).Should().BeNull();
    }

    [Fact]
    public void Check_VetoesWhenAForeignProcessOfTheGameRuns()
    {
        _tracker.FindGameProcesses(Arg.Any<GameInstallation>()).Returns([new TrackedProcess(777, "Game.exe")]);

        GateOver().Check(Context())!.Message.Should().Be("Test Game is running (Game.exe, pid 777). Quit it, then press Play again.");
    }

    [Fact]
    public void Check_LetsAProtectedPidThrough()
    {
        _tracker.FindGameProcesses(Arg.Any<GameInstallation>()).Returns([new TrackedProcess(4242, "silvermodmanager")]);

        GateOver().Check(Context(new HashSet<int> { 4242 })).Should().BeNull();
    }

    [Fact]
    public void Check_LetsOurOwnAncestorThrough()
    {
        // gamescope -- silvermodmanager run %command%: the parent carries the game's prefix variable.
        _tracker.FindGameProcesses(Arg.Any<GameInstallation>()).Returns([new TrackedProcess(3999, "gamescope")]);
        int? ParentOf(int pid) => pid == TestDetectors.OwnPid ? 3999 : null;

        GateOver(ParentOf).Check(Context()).Should().BeNull();
    }

    [Fact]
    public void Check_NamesAnOpenToolInsteadOfTheGame()
    {
        var tools = new StartedTools();
        tools.Register(900, "xedit.exe");
        _tracker.FindGameProcesses(Arg.Any<GameInstallation>()).Returns([new TrackedProcess(900, "wine")]);

        GateOver(tools: tools).Check(Context())!.Message.Should().Be("xedit.exe (pid 900) is open in Test Game's prefix. Close it, then press Play again.");
    }

    [Fact]
    public void Check_NamesTheGameWhenAToolIsOpenToo()
    {
        var tools = new StartedTools();
        tools.Register(900, "xedit.exe");
        _tracker.FindGameProcesses(Arg.Any<GameInstallation>()).Returns([new TrackedProcess(900, "wine"), new TrackedProcess(777, "Game.exe")]);

        GateOver(tools: tools).Check(Context())!.Message.Should().StartWith("Test Game is running (Game.exe, pid 777)");
    }

    [Fact]
    public async Task PrepareAndFinish_DoNothing()
    {
        var gate = GateOver();

        await gate.Prepare(Context(), CancellationToken.None);
        await gate.Finish(Context());

        _tracker.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void TheWording_ForAnOtherProcessAndForATool()
    {
        var game = new RunningProcesses([new TrackedProcess(777, "Game.exe"), new TrackedProcess(778, "wineserver")], []);
        var tool = new RunningProcesses([], [new TrackedProcess(900, "xedit.exe")]);

        RunningGameWording.Outside("Test Game", game).Should().Be("Test Game is running (Game.exe, pid 777, ...). Quit it before applying, deleting or removing.");
        RunningGameWording.ToolsOpen("Test Game", tool).Should().Be("xedit.exe (pid 900) is open in Test Game's prefix. Close it first.");
    }
}
