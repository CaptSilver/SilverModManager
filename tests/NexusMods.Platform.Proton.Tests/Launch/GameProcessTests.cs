using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.Platform.Proton.Tests.Launch;

public class GameProcessTests : IDisposable
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(400);

    private readonly string _dir = Directory.CreateTempSubdirectory("silvermod-gameprocess-").FullName;
    private readonly List<int> _spawned = [];

    public void Dispose()
    {
        // A failing test must not leave sleepers behind.
        foreach (var pid in _spawned) Kill(pid, 9);
        Directory.Delete(_dir, recursive: true);
    }

    private static readonly IReadOnlyDictionary<string, string> BasicEnvironment =
        new Dictionary<string, string> { ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin" };

    private string File_(string name) => Path.Combine(_dir, name);

    private static GameProcessSpec Sh(string script, IReadOnlyDictionary<string, string>? env = null, IReadOnlyList<string>? roots = null)
        => new(["sh", "-c", script], Environment: env, FilesystemRoots: roots);

    private GameInstallation Installation()
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var fs = FileSystem.Shared;
        var folder = fs.FromUnsanitizedFullPath(_dir);
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

    /// <summary>A tracker that reports the pids listed in a file for as long as they are alive.</summary>
    private IProcessTracker TrackerFor(string pidFile)
    {
        var tracker = Substitute.For<IProcessTracker>();
        tracker.FindGameProcesses(Arg.Any<GameInstallation>()).Returns(_ =>
        {
            if (!File.Exists(pidFile)) return [];
            var text = File.ReadAllText(pidFile).Trim();
            if (!int.TryParse(text, out var pid)) return [];
            _spawned.Add(pid);
            return IsAlive(pid) ? new[] { new TrackedProcess(pid, "sleep") } : [];
        });
        return tracker;
    }

    private static bool IsAlive(int pid)
    {
        try { return !File.ReadAllText($"/proc/{pid}/stat").Contains(") Z "); }
        catch (IOException) { return false; }
    }

    private static async Task WaitForFile(string path)
    {
        for (var i = 0; i < 200 && !File.Exists(path); i++) await Task.Delay(25);
        File.Exists(path).Should().BeTrue($"{path} should have been written");
    }

    [DllImport("libc", EntryPoint = "kill")]
    private static extern int Kill(int pid, int signal);

    private GameProcess Start(GameProcessSpec spec, IProcessTracker? tracker = null) =>
        GameProcess.Start(spec, tracker ?? Substitute.For<IProcessTracker>(), Installation(), BasicEnvironment, Grace);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    public async Task ExitCodeOfTheChildPassesThrough(int code)
    {
        using var process = Start(Sh($"exit {code}"));
        (await process.WaitAsync()).ExitCode.Should().Be(code);
    }

    [Fact]
    public async Task ExitCodeIs128PlusTheSignalWhenTheChildCrashes()
    {
        using var process = Start(Sh("kill -SEGV $$"));
        (await process.WaitAsync()).ExitCode.Should().Be(128 + 11);
    }

    [Fact]
    public async Task Terminate_ForwardsSigtermAndTheFixturesTreeEnds()
    {
        // Emulates the reaper: on SIGTERM it stops its own child, then exits.
        var script = $"""
            trap 'kill $child; exit 0' TERM
            sleep 60 >/dev/null 2>&1 </dev/null &
            child=$!
            echo $child > '{File_("child")}'
            echo ready > '{File_("ready")}'
            wait
            """;
        using var process = Start(Sh(script));
        await WaitForFile(File_("ready"));
        var child = int.Parse((await File.ReadAllTextAsync(File_("child"))).Trim());
        _spawned.Add(child);

        process.Terminate();
        var result = await process.WaitAsync();

        result.ExitCode.Should().Be(0);
        await Task.Delay(100);
        IsAlive(child).Should().BeFalse();
    }

    [Theory]
    [InlineData(15)]
    [InlineData(1)]
    public async Task WaitAsync_ForwardsATerminationSignalSentToUs(int signal)
    {
        var script = $"""
            trap 'kill $child; exit 0' TERM HUP
            sleep 60 >/dev/null 2>&1 </dev/null &
            child=$!
            echo $child > '{File_("child")}'
            echo ready > '{File_("ready")}'
            wait
            """;
        using var process = Start(Sh(script));
        await WaitForFile(File_("ready"));
        var child = int.Parse((await File.ReadAllTextAsync(File_("child"))).Trim());
        _spawned.Add(child);

        // The handlers are registered before WaitAsync first yields, so the signal never reaches the
        // runtime's default handling, which would end the test host.
        var wait = process.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Kill(Environment.ProcessId, signal);
        var result = await wait;

        result.ExitCode.Should().Be(0);
        await Task.Delay(100);
        IsAlive(child).Should().BeFalse();
    }

    [Fact]
    public async Task WaitAsync_LeavesTerminationSignalsAloneWhenNotForwarding()
    {
        var script = $"""
            trap 'echo term > '{File_("got-term")}'' TERM HUP
            echo ready > '{File_("ready")}'
            while [ ! -e '{File_("stop")}' ]; do sleep 0.05; done
            """;
        // Stands in for the owner that stops the child itself. Without a handler the signal would end the test host.
        using var owner = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = true);
        using var process = GameProcess.Start(Sh(script), Substitute.For<IProcessTracker>(), Installation(), BasicEnvironment, Grace, forwardTermination: false);
        await WaitForFile(File_("ready"));

        var wait = process.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Kill(Environment.ProcessId, 15);
        await Task.Delay(500);
        await File.WriteAllTextAsync(File_("stop"), "");
        await wait;

        File.Exists(File_("got-term")).Should().BeFalse();
    }

    [Fact]
    public void TheFactoryForwardsTerminationOnlyWhenNothingElseStopsTheGame()
    {
        var tracker = Substitute.For<IProcessTracker>();
        using var stop = new StopHandler(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        new GameProcessFactory(tracker).ForwardTermination.Should().BeTrue();
        new GameProcessFactory(tracker, stop).ForwardTermination.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, false)]
    [InlineData(4242, true)]
    public void OnlyRealProcessesOtherThanInitAreSignalled(int pid, bool expected)
    {
        GameProcess.IsSafeToSignal(pid).Should().Be(expected);
    }

    [Fact]
    public void OurOwnProcessIsNeverSignalled()
    {
        GameProcess.IsSafeToSignal(Environment.ProcessId).Should().BeFalse();
    }

    [Fact]
    public async Task Terminate_KillsAChildThatIgnoresSigterm()
    {
        var script = $"""
            trap '' TERM
            echo ready > '{File_("ready")}'
            while true; do sleep 1; done
            """;
        using var process = Start(Sh(script));
        await WaitForFile(File_("ready"));

        process.Terminate();
        var result = await process.WaitAsync();

        result.ExitCode.Should().Be(128 + 9);
    }

    [Fact]
    public async Task AStragglerTheTrackerReportsIsTerminatedAfterTheChildExits()
    {
        var pidFile = File_("straggler");
        var script = $"sleep 60 >/dev/null 2>&1 </dev/null & echo $! > '{pidFile}'; exit 3";
        using var process = Start(Sh(script), TrackerFor(pidFile));

        var result = await process.WaitAsync();

        result.ExitCode.Should().Be(3);
        result.KillFailed.Should().BeNull();
        var pid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());
        IsAlive(pid).Should().BeFalse();
    }

    [Fact]
    public async Task AStragglerThatIgnoresSigtermIsKilled()
    {
        var pidFile = File_("stubborn");
        var script = $"trap '' TERM; sleep 60 >/dev/null 2>&1 </dev/null & echo $! > '{pidFile}'; exit 0";
        using var process = Start(Sh(script), TrackerFor(pidFile));

        var result = await process.WaitAsync();

        result.KillFailed.Should().BeNull();
        var pid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());
        IsAlive(pid).Should().BeFalse();
    }

    [Fact]
    public async Task ASurvivorOfSigkillIsReported()
    {
        // The tracker keeps reporting a pid that is already gone, as a process stuck in the kernel would.
        var tracker = Substitute.For<IProcessTracker>();
        tracker.FindGameProcesses(Arg.Any<GameInstallation>()).Returns([new TrackedProcess(2147483000, "stuck")]);
        using var process = Start(Sh("exit 0"), tracker);

        var result = await process.WaitAsync();

        result.KillFailed.Should().Be(new TrackedProcess(2147483000, "stuck"));
    }

    [Fact]
    public async Task EnvironmentLosesTheAppImageVariables()
    {
        var env = new Dictionary<string, string>(BasicEnvironment)
        {
            ["APPIMAGE"] = "/x/app.AppImage",
            ["APPDIR"] = "/tmp/.mount_app",
            ["OWD"] = "/home/u",
            ["LD_LIBRARY_PATH"] = "/tmp/.mount_app/usr/lib:/opt/keep",
            ["LD_PRELOAD"] = "/tmp/.mount_app/usr/lib/x.so",
            ["SteamAppId"] = "1091500",
            ["SteamGameId"] = "1091500",
        };
        var script = $"env > '{File_("env")}'";
        using var process = GameProcess.Start(Sh(script), Substitute.For<IProcessTracker>(), Installation(), env, Grace);
        await process.WaitAsync();

        var lines = await File.ReadAllLinesAsync(File_("env"));
        lines.Should().NotContain(l => l.StartsWith("APPIMAGE=") || l.StartsWith("APPDIR=") || l.StartsWith("OWD=") || l.StartsWith("LD_PRELOAD="));
        lines.Should().Contain("LD_LIBRARY_PATH=/opt/keep");
        lines.Should().Contain("SteamAppId=1091500");
        lines.Should().Contain("SteamGameId=1091500");
    }

    [Fact]
    public async Task PressureVesselIsUntouchedForAnEmptyRootList()
    {
        var env = new Dictionary<string, string>(BasicEnvironment) { ["PRESSURE_VESSEL_FILESYSTEMS_RW"] = "/a" };
        using var process = GameProcess.Start(Sh($"env > '{File_("env")}'", roots: []), Substitute.For<IProcessTracker>(), Installation(), env, Grace);
        await process.WaitAsync();

        (await File.ReadAllLinesAsync(File_("env"))).Should().Contain("PRESSURE_VESSEL_FILESYSTEMS_RW=/a");
    }

    [Fact]
    public async Task PressureVesselIsNotSetForAnEmptyRootListWhenItWasUnset()
    {
        using var process = Start(Sh($"env > '{File_("env")}'", roots: []));
        await process.WaitAsync();

        (await File.ReadAllLinesAsync(File_("env"))).Should().NotContain(l => l.StartsWith("PRESSURE_VESSEL_FILESYSTEMS_RW="));
    }

    [Fact]
    public async Task PressureVesselMergesRootsKeepingExistingEntriesAndDroppingDuplicates()
    {
        var env = new Dictionary<string, string>(BasicEnvironment) { ["PRESSURE_VESSEL_FILESYSTEMS_RW"] = "/a:/b" };
        using var process = GameProcess.Start(Sh($"env > '{File_("env")}'", roots: ["/b", "/c", "/c"]), Substitute.For<IProcessTracker>(), Installation(), env, Grace);
        await process.WaitAsync();

        (await File.ReadAllLinesAsync(File_("env"))).Should().Contain("PRESSURE_VESSEL_FILESYSTEMS_RW=/a:/b:/c");
    }

    [Fact]
    public async Task PassThrough_RunsTheArgvUnchanged()
    {
        string[] argv = ["sh", "-c", "printf '%s|' \"$@\" > \"$0\"", File_("argv"), "a b", "", "$HOME", "--x=\"y\""];
        using var process = GameProcess.PassThrough(argv, BasicEnvironment);
        var result = await process.WaitAsync();

        result.ExitCode.Should().Be(0);
        (await File.ReadAllTextAsync(File_("argv"))).Should().Be("a b||$HOME|--x=\"y\"|");
    }
}
