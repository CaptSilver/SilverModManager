using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class PendingLaunchFinisherTests : IDisposable
{
    private static readonly Guid SessionId = Guid.Parse("5b1c1a3e-7d0f-4a52-9a43-3c1b6d1c2f10");
    private static readonly LoadoutId Loadout = LoadoutId.From(1);
    private const int OwnPid = 4000;
    private const int GamePid = 5000;
    private const int HelperPid = 4001;
    private const string AppImage = "/opt/SilverModManager.AppImage";

    private readonly string _runtimeDir = Directory.CreateTempSubdirectory("silvermod-pendingfinish-").FullName;
    private readonly List<string> _events = [];
    private readonly FakeLaunchFinishMarkers _markers = new();
    private readonly ActiveLaunchSessions _sessions = new();
    private readonly IInstallationLockFactory _locks = Substitute.For<IInstallationLockFactory>();
    private readonly IProcessTracker _tracker = Substitute.For<IProcessTracker>();
    private readonly FakeSynchronizer _synchronizer;
    private readonly GateA _gateA;
    private readonly GateB _gateB;
    private readonly GameInstallation _installation;
    private readonly List<LaunchFinishReport> _reports = [];
    private readonly List<InstallationLock> _extraLocks = [];
    private PendingLaunchFinishState _state = PendingLaunchFinishState.Pending;
    private string? _loadoutName = "My Loadout";

    public PendingLaunchFinisherTests()
    {
        _installation = Installation();
        _synchronizer = new FakeSynchronizer(_events, this);
        _gateA = new GateA(_events);
        _gateB = new GateB(_events);
        _locks.Create(Arg.Any<string>(), Arg.Any<string>())
            .Returns(call => new InstallationLock(InstallationLock.PathFor(_runtimeDir, call.ArgAt<string>(0), call.ArgAt<string>(1))));
        _tracker.FindGameProcesses(Arg.Any<GameInstallation>()).Returns(Array.Empty<TrackedProcess>());
        _markers.ReadWith = () => _markers.IsSet
            ? new PendingLaunchFinish(Loadout, _loadoutName, SessionId, _markers.Written!.Value.GateNames, _state)
            : null;
    }

    public void Dispose()
    {
        foreach (var held in _extraLocks) held.Dispose();
        Directory.Delete(_runtimeDir, recursive: true);
    }

    private string LockPath => InstallationLock.PathFor(_runtimeDir, "steam", "42");

    private GameInstallation Installation(bool updatePending = false)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var folder = FileSystem.Shared.FromUnsanitizedFullPath(_runtimeDir);
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

    private PendingLaunchFinisher NewFinisher(OwnProcessFamily? family = null)
    {
        var finish = new LaunchFinish(_synchronizer, _markers, NullLogger<LaunchFinish>.Instance);
        var finisher = new PendingLaunchFinisher(Substitute.For<IConnection>(), _markers, _locks, _tracker, _sessions, [_gateA, _gateB], finish,
            NullLogger<PendingLaunchFinisher>.Instance, family ?? new OwnProcessFamily(_ => null, _ => null, OwnPid), _ => []);
        finisher.Reports.Subscribe(_reports.Add);
        return finisher;
    }

    private Task MarkPending(params string[] gateNames) => _markers.Write(_installation, Loadout, SessionId, gateNames.Length == 0 ? ["GateA"] : gateNames);

    private LaunchPhase? Phase()
    {
        using var reader = new InstallationLock(LockPath);
        return reader.ReadState()?.Phase;
    }

    private void LeaveAbandonedState()
    {
        using var dead = new InstallationLock(LockPath);
        dead.TryAcquire().Should().BeTrue();
        dead.WriteState(99999, "My Loadout", LaunchPhase.Running, "");
    }

    [Fact]
    public async Task FinishIfPending_RunsTheNamedGatesThenOneSyncUnderTheAfterGameRunOriginAndTheAfterPlayLeg()
    {
        await MarkPending("GateA");
        LeaveAbandonedState();
        var seen = new List<(SyncTrigger? Origin, BackupOverflowOperation? Operation, BackupOverflowChoice? Remembered)>();
        _synchronizer.OnSync = () => seen.Add((SyncOrigin.Current, BackupOverflowAnswer.Active?.Operation, BackupOverflowAnswer.Active?.Remembered));

        var report = await NewFinisher().FinishIfPending(_installation);

        _events.Should().Equal("finish:GateA", "sync");
        _gateA.ContextsSeen.Should().Equal((SessionId, false));
        seen.Should().Equal((SyncTrigger.AfterGameRun, BackupOverflowOperation.AfterPlay, BackupOverflowChoice.AdoptInPlace));
        _markers.IsSet.Should().BeFalse();
        File.Exists(new InstallationLock(LockPath).StatePath).Should().BeFalse();
        _sessions.IsRunning(_installation).Should().BeFalse();
        report.Outcome.Should().Be(LaunchFinishOutcome.Finished);
        report.Message.Should().Be("Test Game closed before its changes were taken back into 'My Loadout'. That is done now.");
    }

    [Fact]
    public async Task FinishIfPending_WithoutAMarker_ReturnsNothingPending_AndTouchesNothing()
    {
        var report = await NewFinisher().FinishIfPending(_installation);

        report.Outcome.Should().Be(LaunchFinishOutcome.NothingPending);
        _events.Should().BeEmpty();
        _locks.ReceivedCalls().Should().BeEmpty();
        _tracker.ReceivedCalls().Should().BeEmpty();
        _reports.Should().BeEmpty();
    }

    [Fact]
    public async Task FinishIfPending_WithAMarkerOlderThanTheLastSync_ClearsItWithoutSyncing()
    {
        await MarkPending();
        _state = PendingLaunchFinishState.AlreadySynced;

        var report = await NewFinisher().FinishIfPending(_installation);

        report.Outcome.Should().Be(LaunchFinishOutcome.ClearedAlreadySynced);
        _events.Should().BeEmpty();
        _markers.Retracts.Should().Be(1);
        _markers.IsSet.Should().BeFalse();
        _reports.Should().BeEmpty("the person has nothing to hear about a marker that had been dealt with");
    }

    [Fact]
    public async Task FinishIfPending_WhoseLoadoutIsNotTheLastSynced_ClearsItWithoutRunningGatesTakingALockOrReadingProcesses()
    {
        // The store decides AlreadySynced for both "older than the last sync" and "another loadout was synced since"
        // (LaunchFinishMarkersTests covers the other-loadout case); what this adds is that the finisher does nothing
        // beyond clearing the marker for it.
        await MarkPending();
        _state = PendingLaunchFinishState.AlreadySynced;

        await NewFinisher().FinishIfPending(_installation);

        _gateA.ContextsSeen.Should().BeEmpty();
        _synchronizer.Calls.Should().Be(0);
        _locks.ReceivedCalls().Should().BeEmpty();
        _tracker.ReceivedCalls().Should().BeEmpty();
    }

    [Fact(Timeout = 30_000)]
    public async Task FinishIfPending_WhenReadingTheMarkerThrows_ReportsFailedAndStaysUsable()
    {
        _markers.ReadWith = () => throw new IOException("disk gone");
        var finisher = NewFinisher();

        var report = await finisher.FinishIfPending(_installation);

        report.Outcome.Should().Be(LaunchFinishOutcome.Failed);
        report.Message.Should().Contain("disk gone");
        _reports.Should().ContainSingle().Which.Outcome.Should().Be(LaunchFinishOutcome.Failed);

        _markers.ReadWith = () => null;
        var again = await finisher.FinishIfPending(_installation);
        again.Outcome.Should().Be(LaunchFinishOutcome.NothingPending);
    }

    [Fact]
    public async Task FinishIfPending_WhenTheLockCannotBeOpened_ReportsFailedAndKeepsTheMarker()
    {
        await MarkPending();
        _locks.Create(Arg.Any<string>(), Arg.Any<string>()).Returns(_ => throw new IOException("no runtime dir"));

        var report = await NewFinisher().FinishIfPending(_installation);

        report.Outcome.Should().Be(LaunchFinishOutcome.Failed);
        report.Message.Should().Contain("no runtime dir");
        _markers.IsSet.Should().BeTrue();
        _events.Should().BeEmpty();
        _sessions.IsRunning(_installation).Should().BeFalse();
        _sessions.IsFinishing(_installation).Should().BeFalse();
    }

    [Fact]
    public async Task FinishIfPending_WhenSomethingUnexpectedThrowsAfterTheMarkerIsRead_NamesTheLoadoutAndSaysItIsStillApplied()
    {
        await MarkPending();
        _tracker.FindGameProcesses(Arg.Any<GameInstallation>()).Returns(_ => throw new ArgumentException("odd"));

        var report = await NewFinisher().FinishIfPending(_installation);

        report.Outcome.Should().Be(LaunchFinishOutcome.Failed);
        report.Message.Should().Contain("'My Loadout'").And.Contain("odd");
        report.LoadoutStillApplied.Should().BeTrue();
    }

    [Fact(Timeout = 30_000)]
    public async Task Reports_NeverRunTwoNotificationsAtOnce()
    {
        _markers.ReadWith = () => throw new IOException("disk gone");
        var finisher = NewFinisher();
        var inside = 0;
        var overlapped = false;
        finisher.Reports.Subscribe(_ =>
        {
            if (Interlocked.Increment(ref inside) > 1) overlapped = true;
            Thread.Sleep(150);
            Interlocked.Decrement(ref inside);
        });

        await Task.WhenAll(Task.Run(() => finisher.FinishIfPending(_installation)), Task.Run(() => finisher.FinishIfPending(_installation)));

        overlapped.Should().BeFalse();
    }

    [Fact]
    public async Task FinishIfPending_WhoseLoadoutIsGone_ClearsItAndSaysSo()
    {
        await MarkPending();
        _state = PendingLaunchFinishState.LoadoutGone;
        _loadoutName = null;

        var report = await NewFinisher().FinishIfPending(_installation);

        report.Outcome.Should().Be(LaunchFinishOutcome.ClearedLoadoutGone);
        report.Message.Should().Be("Test Game closed before its changes were taken back into its loadout, and that loadout has since been deleted. The files are still in the game folder.");
        _events.Should().BeEmpty();
        _markers.IsSet.Should().BeFalse();
        _reports.Should().ContainSingle();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FinishIfPending_WhileSteamIsUpdating_KeepsTheMarker(bool manifestSaysSo)
    {
        await MarkPending();
        var installation = Installation(updatePending: manifestSaysSo);
        if (!manifestSaysSo) _tracker.IsInstallStepRunning(42u).Returns(true);

        var report = await NewFinisher().FinishIfPending(installation);

        report.Outcome.Should().Be(LaunchFinishOutcome.WaitingForSteam);
        report.Message.Should().Be("Steam is updating Test Game, so the changes from your last session are not in 'My Loadout' yet. The app finishes this after the update, the next time it applies or launches.");
        _markers.IsSet.Should().BeTrue();
        _locks.ReceivedCalls().Should().BeEmpty("no lock is taken while Steam is updating");
    }

    [Fact]
    public async Task FinishIfPending_WhileTheTrackerSeesAGameProcess_KeepsTheMarker()
    {
        await MarkPending();
        _tracker.FindGameProcesses(Arg.Any<GameInstallation>()).Returns([new TrackedProcess(GamePid, "game.exe")]);

        var report = await NewFinisher().FinishIfPending(_installation);

        report.Outcome.Should().Be(LaunchFinishOutcome.GameRunning);
        report.Message.Should().Be("Test Game is still running, so the changes from your last session are not in 'My Loadout' yet. Quit the game; the app finishes this the next time it applies or launches.");
        _markers.IsSet.Should().BeTrue();
        _events.Should().BeEmpty();
    }

    [Fact]
    public async Task FinishIfPending_WhenTheTrackerSeesOnlyOurOwnFamily_Finishes()
    {
        await MarkPending();
        var appImage = ProcessAppImage();
        _tracker.FindGameProcesses(Arg.Any<GameInstallation>()).Returns([new TrackedProcess(OwnPid, "silvermod"), new TrackedProcess(HelperPid, "AppImage")]);
        var family = new OwnProcessFamily(name => name == "APPIMAGE" ? AppImage : null, pid => pid == HelperPid ? appImage : null, OwnPid);

        var report = await NewFinisher(family).FinishIfPending(_installation);

        report.Outcome.Should().Be(LaunchFinishOutcome.Finished);
    }

    private static string ProcessAppImage() => ProcProcessTracker.RealPath(AppImage);

    [Fact]
    public async Task FinishIfPending_WhileAnotherLockInstanceHoldsTheGame_SkipsAndKeepsTheMarker()
    {
        await MarkPending();
        var other = new InstallationLock(LockPath);
        _extraLocks.Add(other);
        other.TryAcquire().Should().BeTrue();

        var report = await NewFinisher().FinishIfPending(_installation);

        report.Outcome.Should().Be(LaunchFinishOutcome.SkippedHeld);
        _markers.IsSet.Should().BeTrue();
        _events.Should().BeEmpty();
        _reports.Should().BeEmpty();
        _sessions.IsRunning(_installation).Should().BeFalse();
    }

    [Fact]
    public async Task FinishIfPending_WhileThisProcessRunsALaunchOfTheGame_SkipsWithoutReadingProcesses()
    {
        await MarkPending();
        using var registration = _sessions.Register(_installation);

        var report = await NewFinisher().FinishIfPending(_installation);

        report.Outcome.Should().Be(LaunchFinishOutcome.SkippedHeld);
        _tracker.ReceivedCalls().Should().BeEmpty();
        _locks.ReceivedCalls().Should().BeEmpty();
        _markers.IsSet.Should().BeTrue();
    }

    [Fact]
    public async Task FinishIfPending_WithoutAStateFile_StillFinishes()
    {
        await MarkPending();
        File.Exists(new InstallationLock(LockPath).StatePath).Should().BeFalse();

        var report = await NewFinisher().FinishIfPending(_installation);

        report.Outcome.Should().Be(LaunchFinishOutcome.Finished);
    }

    [Fact]
    public async Task FinishIfPending_WhoseSyncThrows_RetractsTheMarkerAndReleasesEverything()
    {
        await MarkPending();
        LeaveAbandonedState();
        _synchronizer.FailWith = new InvalidOperationException("sync broke");

        var report = await NewFinisher().FinishIfPending(_installation);

        report.Outcome.Should().Be(LaunchFinishOutcome.Failed);
        report.Message.Should().Contain("is still applied");
        report.LoadoutStillApplied.Should().BeTrue();
        _markers.IsSet.Should().BeFalse();
        using var probe = new InstallationLock(LockPath);
        probe.Probe().Should().BeFalse("the lock is free");
        File.Exists(probe.StatePath).Should().BeFalse();
        _sessions.IsRunning(_installation).Should().BeFalse();
        _reports.Should().ContainSingle().Which.Outcome.Should().Be(LaunchFinishOutcome.Failed);
    }

    [Fact]
    public async Task FinishIfPending_ReportsFinishingWhileItRuns()
    {
        await MarkPending();
        var finishing = new List<bool>();
        var phases = new List<LaunchPhase?>();
        _synchronizer.OnSync = () =>
        {
            finishing.Add(_sessions.IsFinishing(_installation));
            phases.Add(Phase());
        };

        await NewFinisher().FinishIfPending(_installation);

        finishing.Should().Equal(true);
        phases.Should().Equal(LaunchPhase.Finishing);
    }

    [Fact]
    public async Task FinishIfPending_WithAHeldLock_UsesItAndLeavesItToItsOwner()
    {
        await MarkPending();
        using var held = new InstallationLock(LockPath);
        held.TryAcquire().Should().BeTrue();
        var registered = new List<bool>();
        _synchronizer.OnSync = () => registered.Add(_sessions.IsRunning(_installation));

        var report = await NewFinisher().FinishIfPending(_installation, held, callerReports: true);

        report.Outcome.Should().Be(LaunchFinishOutcome.Finished);
        held.IsHeld.Should().BeTrue();
        registered.Should().Equal(false);
        _reports.Should().BeEmpty("the caller of the lock-holding entry shows the report itself");
    }

    [Fact(Timeout = 30_000)]
    public async Task TwoConcurrentCalls_RunOneFinish()
    {
        await MarkPending();
        _synchronizer.Delay = TimeSpan.FromMilliseconds(200);
        var finisher = NewFinisher();

        var reports = await Task.WhenAll(Task.Run(() => finisher.FinishIfPending(_installation)), Task.Run(() => finisher.FinishIfPending(_installation)));

        _events.Count(e => e == "sync").Should().Be(1);
        reports.Select(r => r.Outcome).Should().BeEquivalentTo([LaunchFinishOutcome.Finished, LaunchFinishOutcome.NothingPending]);
    }

    [Fact]
    public async Task TheFourMessages_ReadAsWritten()
    {
        await MarkPending();
        var finisher = NewFinisher();

        _tracker.FindGameProcesses(Arg.Any<GameInstallation>()).Returns([new TrackedProcess(GamePid, "game.exe")]);
        (await finisher.FinishIfPending(_installation)).Message.Should().Be("Test Game is still running, so the changes from your last session are not in 'My Loadout' yet. Quit the game; the app finishes this the next time it applies or launches.");

        _state = PendingLaunchFinishState.LoadoutGone;
        _tracker.FindGameProcesses(Arg.Any<GameInstallation>()).Returns(Array.Empty<TrackedProcess>());
        (await finisher.FinishIfPending(_installation)).Message.Should().Be("Test Game closed before its changes were taken back into 'My Loadout', and that loadout has since been deleted. The files are still in the game folder.");

        await MarkPending();
        _state = PendingLaunchFinishState.Pending;
        (await finisher.FinishIfPending(_installation)).Message.Should().Be("Test Game closed before its changes were taken back into 'My Loadout'. That is done now.");

        await MarkPending();
        (await finisher.FinishIfPending(Installation(updatePending: true))).Message.Should().Be("Steam is updating Test Game, so the changes from your last session are not in 'My Loadout' yet. The app finishes this after the update, the next time it applies or launches.");
    }

    private sealed class FakeSynchronizer(List<string> events, PendingLaunchFinisherTests test) : ILaunchSynchronizer
    {
        public int Calls { get; private set; }
        public Action? OnSync { get; set; }
        public Exception? FailWith { get; set; }
        public TimeSpan Delay { get; set; }

        public async Task Synchronize(LoadoutId loadoutId)
        {
            lock (events) events.Add("sync");
            Calls++;
            OnSync?.Invoke();
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay);
            if (FailWith is not null) throw FailWith;
        }
    }

    private abstract class RecordingGate(List<string> events) : ILaunchGate
    {
        public List<(Guid SessionId, bool EndSeen)> ContextsSeen { get; } = [];
        public Veto? Check(LaunchContext context) => null;
        public Task Prepare(LaunchContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task Finish(LaunchContext context)
        {
            lock (events) events.Add($"finish:{GetType().Name}");
            ContextsSeen.Add((context.SessionId, context.EndSeen));
            return Task.CompletedTask;
        }
    }

    private sealed class GateA(List<string> events) : RecordingGate(events);

    private sealed class GateB(List<string> events) : RecordingGate(events);
}
