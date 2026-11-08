using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using NexusMods.Paths;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SilverModManager.Games.TestFramework;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class StragglerShutdownTests
{
    private const int Forwarder = 500;
    private const int ForwarderShell = 400;
    private const int Game = 600;
    private const int SigTerm = 15;
    private const int SigKill = 9;
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(100);

    private static GameInstallation Installation()
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var folder = FileSystem.Shared.FromUnsanitizedFullPath("/tmp/silvermod-straggler-shutdown");
        var result = new GameLocatorResult
        {
            Game = game,
            Path = folder,
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, folder)));
    }

    // Every process lives until it is sent SIGKILL, and the signals sent are recorded instead of delivered.
    private sealed class Rig
    {
        private readonly HashSet<int> _alive;
        public readonly List<(int Pid, int Signal)> Signals = [];
        public readonly FakeProcessTracker Tracker = new();

        public Rig(params int[] pids)
        {
            _alive = [..pids];
            Tracker.ForInstallation = _ => _alive.Select(pid => new TrackedProcess(pid, "p" + pid)).ToList();
        }

        public int Signal(int pid, int signal)
        {
            lock (Signals) Signals.Add((pid, signal));
            if (signal == SigKill) _alive.Remove(pid);
            return 0;
        }
    }

    [Fact]
    public async Task AGameProcessThatOutlivesItsGraceGetsSigtermThenSigkill()
    {
        var rig = new Rig(Game);

        var survivor = await StragglerShutdown.RunAsync(rig.Tracker, Installation(), Grace, CancellationToken.None, signal: rig.Signal);

        survivor.Should().BeNull();
        rig.Signals.Should().Equal((Game, SigTerm), (Game, SigKill));
    }

    [Fact]
    public async Task TheForwardingClientAndItsAncestorsAreNeverSignalled_WhileTheGameStillIs()
    {
        // The forwarding client carries the game's prefix in its environment, as does the shell above it, so the
        // tracker lists both next to the game. They are waiting to be told the game's exit, not leftovers.
        var rig = new Rig(Forwarder, ForwarderShell, Game);
        IReadOnlySet<int> excluded = new HashSet<int> { Forwarder };

        var survivor = await StragglerShutdown.RunAsync(rig.Tracker, Installation(), Grace, CancellationToken.None,
            excluded, rig.Signal, parentOf: pid => pid == Forwarder ? ForwarderShell : null);

        rig.Signals.Should().NotContain(s => s.Pid == Forwarder || s.Pid == ForwarderShell);
        rig.Signals.Should().Equal((Game, SigTerm), (Game, SigKill));
        survivor.Should().BeNull("the excluded processes are not the game's, so they are not counted as survivors");
    }

    private const int Helper = 700;
    private const int OwnPid = 4321;
    // Reached through a symlink on any machine, as /home is on ostree hosts: $APPIMAGE may be spelled with links,
    // but the kernel reports the resolved path in /proc/<pid>/exe.
    private const string AppImage = "/proc/self/root/opt/SilverModManager.AppImage";
    private static readonly string AppImageExe = ProcProcessTracker.RealPath(AppImage);

    private static OwnProcessFamily Family(string? appImage, string? helperExe = null)
        => new(name => name == "APPIMAGE" ? appImage : null, pid => pid == Helper ? helperExe ?? AppImageExe : null, OwnPid);

    [Theory]
    [InlineData("")]
    [InlineData(" (deleted)")]
    public async Task TheAppImageMountHelperIsNeverSignalledNorCountedAsAStraggler(string suffix)
    {
        var helperExe = AppImageExe + suffix;
        // The helper carries the game's prefix in its environment when we were started from a Steam launch option,
        // so the tracker lists it next to the game. It is the mount we run from.
        var rig = new Rig(Helper, Game);

        var survivor = await StragglerShutdown.RunAsync(rig.Tracker, Installation(), Grace, CancellationToken.None,
            signal: rig.Signal, family: Family(AppImage, helperExe));

        rig.Signals.Should().Equal((Game, SigTerm), (Game, SigKill));
        survivor.Should().BeNull("the helper is not the game's, so it is not a survivor");
    }

    [Fact]
    public async Task WithTheHelperAloneTheSweepEndsAtOnce()
    {
        var rig = new Rig(Helper);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var survivor = await StragglerShutdown.RunAsync(rig.Tracker, Installation(), TimeSpan.FromSeconds(30), CancellationToken.None,
            signal: rig.Signal, family: Family(AppImage));

        survivor.Should().BeNull();
        rig.Signals.Should().BeEmpty();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "nothing is left to wait for");
    }

    [Fact]
    public async Task WithoutAnAppImageTheHelperLookalikeIsSignalledAsBefore()
    {
        var rig = new Rig(Helper);

        var survivor = await StragglerShutdown.RunAsync(rig.Tracker, Installation(), Grace, CancellationToken.None,
            signal: rig.Signal, family: Family(appImage: null));

        survivor.Should().BeNull();
        rig.Signals.Should().Equal((Helper, SigTerm), (Helper, SigKill));
    }

    [Fact]
    public async Task OurOwnPidIsNeverSignalled_WhenTheTrackerListsIt()
    {
        var rig = new Rig(OwnPid, Game);

        await StragglerShutdown.RunAsync(rig.Tracker, Installation(), Grace, CancellationToken.None,
            signal: rig.Signal, family: Family(appImage: null));

        rig.Signals.Should().Equal((Game, SigTerm), (Game, SigKill));
    }

    [Fact]
    public async Task EachSignalIsLoggedAtTraceWithThePidAndName()
    {
        var rig = new Rig(Game);
        var log = new RecordingLogger();

        await StragglerShutdown.RunAsync(rig.Tracker, Installation(), Grace, CancellationToken.None,
            signal: rig.Signal, family: Family(appImage: null), logger: log);

        var trace = log.Entries.Where(e => e.Level == LogLevel.Trace).Select(e => e.Message).ToList();
        trace.Should().HaveCount(2);
        trace[0].Should().Contain($"{Game}").And.Contain("p600").And.Contain("SIGTERM");
        trace[1].Should().Contain($"{Game}").And.Contain("p600").And.Contain("SIGKILL");
    }

    [Fact]
    public async Task EachWaitIsLoggedAtDebugWithTheCountAndGrace()
    {
        var rig = new Rig(Game);
        var log = new RecordingLogger();

        await StragglerShutdown.RunAsync(rig.Tracker, Installation(), Grace, CancellationToken.None,
            signal: rig.Signal, family: Family(appImage: null), logger: log);

        var debug = log.Entries.Where(e => e.Level == LogLevel.Debug).Select(e => e.Message).ToList();
        debug.Should().HaveCount(3, "one wait before any signal and one after each signal");
        debug.Should().OnlyContain(m => m == $"Waiting up to {Grace.TotalMilliseconds:0} ms for 1 game process(es) to leave: p{Game} ({Game})");
    }

    [Fact]
    public async Task NothingIsLoggedWhenNothingIsLeft()
    {
        var log = new RecordingLogger();

        await StragglerShutdown.RunAsync(new Rig().Tracker, Installation(), Grace, CancellationToken.None, family: Family(AppImage), logger: log);

        log.Entries.Should().BeEmpty();
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void TheParentOfThisProcessIsReadFromProc()
    {
        StragglerShutdown.ParentOf(Environment.ProcessId).Should().BePositive();
    }
}
