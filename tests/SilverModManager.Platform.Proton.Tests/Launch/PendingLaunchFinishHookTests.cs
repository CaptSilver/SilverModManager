using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.DataModel.Synchronizer;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Tests.Launch;

/// <summary>
/// A sync of a loadout first finishes the previous launch of its game if one is still pending, so a person who
/// applies or launches after the app was killed mid-session does not lose that session's changes.
/// </summary>
public class PendingLaunchFinishHookTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<PendingLaunchFinishHookTests>(helper)
{
    private readonly string _runtimeDir = Directory.CreateTempSubdirectory("silvermod-finishhook-").FullName;
    private readonly LineCollector _logs = new();

    private const string GameClosing = "started by the game closing";

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        // Registered before AddHeadlessLaunch, which only adds what is missing: the real lock factory would read
        // the host's XDG_RUNTIME_DIR, and the real notifier would talk to the desktop.
        var locks = Substitute.For<IInstallationLockFactory>();
        locks.Create(Arg.Any<string>(), Arg.Any<string>())
            .Returns(call => new InstallationLock(InstallationLock.PathFor(_runtimeDir, call.ArgAt<string>(0), call.ArgAt<string>(1))));
        services.AddSingleton(locks);
        services.AddSingleton(Substitute.For<IDesktopNotifier>());
        services.AddSingleton<ILoggerProvider>(_logs);
        services.Configure<LoggerFilterOptions>(options => options.MinLevel = LogLevel.Trace);
        return base.AddServices(services).AddHeadlessLaunch();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(_runtimeDir, recursive: true); } catch (IOException) { }
    }

    private ILaunchFinishMarkers Markers => ServiceProvider.GetRequiredService<ILaunchFinishMarkers>();

    private async Task<LoadoutId> LoadoutWithPendingFinish()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = (await CreateLoadout()).LoadoutId;
        await Markers.Write(GameInstallation, loadout, Guid.NewGuid(), []);
        Markers.Read(GameInstallation)!.State.Should().Be(PendingLaunchFinishState.Pending);
        return loadout;
    }

    private int Closings => _logs.Messages.Count(m => m.Contains(GameClosing));

    private EntityId MetadataId => GameRegistry.ForceGetMetadata(GameInstallation).Id;

    private AbsolutePath GameDir => GameInstallation.Locations[LocationId.Game].Path;

    private bool IsAttribute(Datom d, IAttribute attribute) => d.A == Connection.Db.AttributeCache.GetAttributeId(attribute.Id);

    /// <summary>The commits after <paramref name="before"/> that recorded a sync, oldest first.</summary>
    private List<TxId> SyncCommitsAfter(TxId before)
        => Enumerable.Range(1, (int)(Connection.Db.BasisTxId.Value - before.Value))
            .Select(i => TxId.From(before.Value + (ulong)i))
            .Where(t => Connection.AsOf(t).Datoms(t).Any(d => d.E == MetadataId && !d.IsRetract && IsAttribute(d, GameInstallMetadata.LastSyncedLoadoutTransaction)))
            .ToList();

    private bool RetractsMarker(TxId tx)
        => Connection.AsOf(tx).Datoms(tx).Any(d => d.E == MetadataId && d.IsRetract && IsAttribute(d, GameInstallMetadata.LaunchFinishPending));

    private bool LoadoutHolds(LoadoutId loadoutId, string fileName)
        => LoadoutItem.FindByLoadout(Connection.Db, Loadout.Load(Connection.Db, loadoutId)).OfTypeLoadoutItemWithTargetPath()
            .Any(f => ((GamePath)f.TargetPath).Path.FileName.ToString() == fileName);

    [Fact]
    public async Task SynchronizeCore_RunsThePendingFinishBeforeTheCallersSync()
    {
        var loadout = await LoadoutWithPendingFinish();
        var before = Connection.Db.BasisTxId;

        await SynchronizerService.Synchronize(loadout);

        Markers.Read(GameInstallation).Should().BeNull();
        var messages = _logs.Messages;
        var finish = messages.FindIndex(m => m.Contains(GameClosing));
        finish.Should().BeGreaterThanOrEqualTo(0, "the pending finish syncs under the game-closing origin");
        messages.Skip(finish + 1).Should().Contain(m => m.StartsWith("Starting sync of loadout"),
            "the caller's own sync starts after the finish's");
        var commits = SyncCommitsAfter(before);
        commits.Should().HaveCount(2, "the finish's sync and the caller's each record a sync");
        RetractsMarker(commits[0]).Should().BeTrue("the finish's sync, which came first, is the one that took the marker");
    }

    [Fact]
    public async Task SynchronizeCore_WhileALaunchInThisProcessHoldsTheLock_SkipsAndStillSyncs()
    {
        var loadout = await LoadoutWithPendingFinish();
        var store = GameInstallation.LocatorResult.Store.Value.ToLowerInvariant();
        using var held = new InstallationLock(InstallationLock.PathFor(_runtimeDir, store, GameInstallation.LocatorResult.StoreIdentifier));
        held.TryAcquire().Should().BeTrue();
        var before = Connection.Db.BasisTxId;

        await SynchronizerService.Synchronize(loadout);

        Closings.Should().Be(0, "the holder owns the finish");
        _logs.Messages.Should().Contain(m => m.StartsWith("Starting sync of loadout"), "the caller's sync ran");
        SyncCommitsAfter(before).Should().ContainSingle("the caller's sync committed and no finish did");
    }

    [Fact(Timeout = 60_000)]
    public async Task FinishIfPending_WhoseOwnSyncPassesThroughTheHook_RunsOneFinish()
    {
        await LoadoutWithPendingFinish();
        var finisher = ServiceProvider.GetRequiredService<IPendingLaunchFinisher>();

        var report = await finisher.FinishIfPending(GameInstallation, TestContext.Current.CancellationToken);

        report.Outcome.Should().Be(LaunchFinishOutcome.Finished);
        Closings.Should().Be(1);
    }

    [Fact]
    public async Task CleanGameFolder_FinishesFirstThenCleans()
    {
        var loadout = await LoadoutWithPendingFinish();
        var written = GameDir / "session-write.txt";
        written.Parent.CreateDirectory();
        await written.WriteAllTextAsync("written while the game ran");

        await SynchronizerService.CleanGameFolder(loadout);

        Markers.Read(GameInstallation).Should().BeNull();
        var messages = _logs.Messages;
        var finish = messages.FindIndex(m => m.Contains(GameClosing));
        finish.Should().BeGreaterThanOrEqualTo(0);
        messages.Skip(finish + 1).Should().Contain(m => m.StartsWith("Starting") && m.Contains("putting the game folder back"));
        LoadoutHolds(loadout, "session-write.txt").Should().BeTrue("the finish took the game's write into the loadout before the clean");
        written.FileExists.Should().BeFalse("the clean took the game's write out of the folder, after the finish had kept it in the loadout");
    }

    [Fact]
    public async Task SynchronizeCore_InsideAnOperationThatHoldsNoAnswerYet_LeavesTheOperationsQuestionToItsOwnLegs()
    {
        var loadout = await LoadoutWithPendingFinish();
        using var switching = BackupOverflowAnswer.Begin(BackupOverflowOperation.Switch);

        await SynchronizerService.Synchronize(loadout);

        Closings.Should().Be(1);
        BackupOverflowAnswer.Active!.Operation.Should().Be(BackupOverflowOperation.Switch);
        BackupOverflowAnswer.Active.Remembered.Should().BeNull("the finish's keep-in-place default was its own, so the switch still asks");
    }

    private sealed class LineCollector : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();
        public List<string> Messages => _lines.ToList();
        public ILogger CreateLogger(string categoryName) => new Sink(_lines);
        public void Dispose() { }

        private sealed class Sink(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => lines.Enqueue(formatter(state, exception));
        }
    }
}
