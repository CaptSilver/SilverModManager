using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Games.TestFramework;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests.Processes;

public class RunningGameDetectorTests
{
    private const int OwnPid = 500;
    private const int Helper = 700;
    private const string AppImage = "/proc/self/root/opt/SilverModManager.AppImage";
    private static readonly string AppImageExe = ProcProcessTracker.RealPath(AppImage);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(8);

    private readonly FakeProcessTracker _tracker = new();
    private readonly StartedTools _tools = new();
    private readonly FakeTimeProvider _clock = new();
    private readonly Dictionary<int, int> _parents = [];

    private static GameInstallation Installation()
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var folder = FileSystem.Shared.FromUnsanitizedFullPath("/tmp/silvermod-running-detector");
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

    private RunningGameDetector Detector(string? appImage = null)
    {
        var family = new OwnProcessFamily(name => name == "APPIMAGE" ? appImage : null, pid => pid == Helper ? AppImageExe : null, OwnPid);
        return new RunningGameDetector(_tracker, _tools, _clock, null, pid => _parents.TryGetValue(pid, out var parent) ? parent : null, family, OwnPid, Interval);
    }

    private void Lists(params int[][] scans)
    {
        var calls = 0;
        _tracker.ForInstallation = _ =>
        {
            var scan = scans[Math.Min(calls++, scans.Length - 1)];
            return scan.Select(pid => new TrackedProcess(pid, "p" + pid)).ToList();
        };
    }

    private static int[] Pids(IReadOnlyList<TrackedProcess> processes) => processes.Select(p => p.Pid).Order().ToArray();

    // The wait sleeps on the fake clock, so the test moves time forward until it ends.
    private async Task<T> Drive<T>(Task<T> wait)
    {
        while (!wait.IsCompleted)
        {
            _clock.Advance(Interval);
            await Task.Yield();
        }
        return await wait;
    }

    [Fact]
    public void Running_NeverReturnsOurOwnPidOrTheAppImageHelper()
    {
        Lists([OwnPid, Helper, 777]);

        Pids(Detector(AppImage).Running(Installation()).Game).Should().Equal(777);
    }

    [Fact]
    public void Running_NeverReturnsOurOwnAncestors()
    {
        // Under "gamescope -- silvermodmanager run %command%" the parents carry the prefix variable too.
        _parents[OwnPid] = 400;
        _parents[400] = 300;
        Lists([300, 400, 777]);

        Pids(Detector().Running(Installation()).Game).Should().Equal(777);
    }

    [Fact]
    public void Running_DropsAProtectedPidAndItsAncestors()
    {
        _parents[900] = 800;
        Lists([800, 900, 777]);

        Pids(Detector().Running(Installation(), new HashSet<int> { 900 }).Game).Should().Equal(777);
    }

    [Fact]
    public void Running_PutsAStartedToolAndItsChildInTools()
    {
        using var registration = _tools.Register(600, "xEdit");
        _parents[601] = 600;
        Lists([600, 601, 777]);

        var running = Detector().Running(Installation());

        Pids(running.Tools).Should().Equal(600, 601);
        Pids(running.Game).Should().Equal(777);
    }

    [Fact]
    public void Running_IsEmptyWhenNothingRuns()
    {
        Lists(Array.Empty<int>());

        var running = Detector().Running(Installation());

        running.Any.Should().BeFalse();
    }

    [Fact]
    public async Task WaitUntilGone_DoesNotEndWhileAProcessReappearsInsideTheSettlePeriod()
    {
        // A launcher that exits before its game appears leaves an empty scan in the middle.
        Lists([11], [], [12], [], [], [], []);
        var start = _clock.GetUtcNow();

        var result = await Drive(Detector().WaitUntilGone(Installation(), new GoneWait(Settle, Interval: Interval), CancellationToken.None));

        result.Any.Should().BeFalse();
        (_clock.GetUtcNow() - start).Should().Be(TimeSpan.FromSeconds(12), "the settle period restarts at the second sighting, at 4 s");
    }

    [Fact]
    public async Task WaitUntilGone_EndsAfterSettleOfSilence()
    {
        Lists(Array.Empty<int>());
        var start = _clock.GetUtcNow();

        var result = await Drive(Detector().WaitUntilGone(Installation(), new GoneWait(Settle, Interval: Interval), CancellationToken.None));

        result.Should().BeSameAs(RunningProcesses.Empty);
        (_clock.GetUtcNow() - start).Should().Be(Settle);
    }

    [Fact]
    public async Task WaitUntilGone_ASteamRelaunchWithANewReaperPidAndNoGapIsOneRun()
    {
        Lists([11], [12], [12], []);
        var start = _clock.GetUtcNow();

        var result = await Drive(Detector().WaitUntilGone(Installation(), new GoneWait(Settle, Interval: Interval), CancellationToken.None));

        result.Any.Should().BeFalse();
        (_clock.GetUtcNow() - start).Should().Be(TimeSpan.FromSeconds(12), "only the silence after the last sighting, at 4 s, counts");
    }

    [Fact]
    public async Task WaitUntilGone_ReturnsWhatIsRunningAtOnceWhenTheStopIsSet()
    {
        Lists([777]);
        var stop = new StopRequest();
        stop.Set();

        var result = await Detector().WaitUntilGone(Installation(), new GoneWait(Settle, Stop: stop, Interval: Interval), CancellationToken.None);

        Pids(result.Game).Should().Equal(777);
    }

    [Fact]
    public async Task WaitUntilGone_ReportsEachNonEmptyScanToProgress()
    {
        Lists([11], [], [12, 13], []);
        var seen = new List<int[]>();

        await Drive(Detector().WaitUntilGone(Installation(), new GoneWait(Settle, Interval: Interval, Progress: new Inline<RunningProcesses>(r => seen.Add(Pids(r.Game)))), CancellationToken.None));

        seen.Should().HaveCount(2);
        seen[0].Should().Equal(11);
        seen[1].Should().Equal(12, 13);
    }

    [Fact]
    public async Task WaitUntilGone_StopsWhenCancelled()
    {
        Lists([777]);
        using var cancel = new CancellationTokenSource();
        var wait = Detector().WaitUntilGone(Installation(), new GoneWait(Settle, Interval: Interval), cancel.Token);

        await cancel.CancelAsync();

        await wait.Invoking(w => w).Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class Inline<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }
}
