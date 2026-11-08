using System.Collections.Immutable;
using System.Text;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Settings;
using SilverModManager.DataModel;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A finish the process did not survive leaves the pending-finish marker behind and the files the game wrote
/// on disk. The redo runs the same ingest again, so it has to take in what the first attempt had not recorded,
/// and neither duplicate nor delete what it had already put in place.
/// </summary>
/// <remarks>
/// The interruption here is a throw in the backend's timestamp phase: after every disk write and before the
/// transaction commits, the latest point at which a sync can die with nothing recorded. Archives are written as
/// <c>&lt;guid&gt;.tmp</c> and renamed to <c>.nx</c> (<c>NxFileStore.BackupFiles</c>), and archives are listed by
/// <c>.nx</c> only, so a <c>.tmp</c> orphaned by a kill mid-write is never read as an archive. Nothing sweeps
/// such a file; the garbage collector's repacker uses the same write-then-rename. That sweep is not part of this
/// test.
/// </remarks>
public class InterruptedIngestRedoTests : AIsolatedGameTest<InterruptedIngestRedoTests, InterruptedIngestGame>
{
    private static readonly DateTimeOffset T0 = new(2024, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly GamePath GameRoot = new(LocationId.Game, "");
    private static readonly GamePath Plugin = new(LocationId.Game, "plugins/a.esp");

    public InterruptedIngestRedoTests(ITestOutputHelper helper) : base(helper) => InterruptedIngestGame.Order = [];

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        services.TryAddSingleton<FaultingDeploymentBackend>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDeploymentBackend, FaultingDeploymentBackend>(sp => sp.GetRequiredService<FaultingDeploymentBackend>()));
        return base.AddServices(services)
            .AddGame<InterruptedIngestGame>()
            .AddUniversalGameLocator<InterruptedIngestGame>(Version.Parse("0.0.0"));
    }

    private FaultingDeploymentBackend Faulting => ServiceProvider.GetRequiredService<FaultingDeploymentBackend>();

    private EntityId MetadataId => GameRegistry.ForceGetMetadata(GameInstallation).Id;

    private AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    private static GamePath Written(int i) => new(LocationId.Game, $"extra/written{i}.txt");

    private int ArchiveCount() => ServiceProvider.GetRequiredService<ISettingsManager>()
        .Get<DataModelSettings>().ArchiveLocations
        .SelectMany(l => Directory.GetFiles(l.ToPath(FileSystem).GetFullPath(), "*.nx"))
        .Count();

    private async Task<Loadout.ReadOnly> LoadoutWithADeployedPlugin()
    {
        var loadout = await CreateLoadout();
        using (var route = Connection.BeginTransaction())
        {
            ServiceProvider.GetRequiredService<IDeploymentPlanStore>()
                .Write(route, Connection.Db, MetadataId, GameRoot, FaultingDeploymentBackend.BackendId, chosen: true);
            await route.Commit();
        }

        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)Plugin.Path], loadout.LoadoutId, "plugins");
            await tx.Commit();
        }

        InterruptedIngestGame.Order = [(Plugin, T0)];
        await SynchronizerService.Synchronize(loadout.LoadoutId);
        return Refresh(loadout);
    }

    private void TheGameWrites(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var file = OnDisk(Written(i));
            file.Parent.CreateDirectory();
            File.WriteAllText(file.GetFullPath(), $"written by the game {i}");
        }
    }

    private async Task MarkAFinishPending(Loadout.ReadOnly loadout)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(MetadataId, GameInstallMetadata.LaunchFinishPending, loadout.Id);
        tx.Add(MetadataId, GameInstallMetadata.LaunchFinishPendingSession, Guid.NewGuid().ToString());
        await tx.Commit();
    }

    private static async Task AsTheFinish(Func<Task> sync)
    {
        using var origin = SyncOrigin.Begin(SyncTrigger.AfterGameRun);
        using var leg = BackupOverflowAnswer.BeginLeg(BackupOverflowOperation.AfterPlay, BackupOverflowChoice.AdoptInPlace);
        await sync();
    }

    private List<GamePath> LoadoutPaths(Loadout.ReadOnly loadout) =>
        LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Select(f => (GamePath)f.TargetPath).ToList();

    [Fact]
    public async Task ARedoAfterAFaultBeforeTheCommit_IngestsEachFileOnceAndDeletesNothing()
    {
        const int count = 3;
        var loadout = await LoadoutWithADeployedPlugin();
        TheGameWrites(count);
        await MarkAFinishPending(loadout);
        Faulting.FailStampingOf = Plugin;
        var first = async () => await AsTheFinish(() => SynchronizerService.Synchronize(loadout.LoadoutId));
        await first.Should().ThrowAsync<IOException>("the backend dies after the writes and before the commit");

        Faulting.FailStampingOf = null;
        var archivesAfterTheFault = ArchiveCount();
        var recordedByTheFault = LoadoutPaths(Refresh(loadout));
        for (var i = 0; i < count; i++)
        {
            OnDisk(Written(i)).FileExists.Should().BeTrue("the failed attempt deleted nothing");
            recordedByTheFault.Should().NotContain(Written(i), "nothing was recorded by the failed attempt");
        }
        OnDisk(Plugin).FileExists.Should().BeTrue();
        GameInstallMetadata.Load(Connection.Db, MetadataId).Contains(GameInstallMetadata.LaunchFinishPending)
            .Should().BeTrue("the failed attempt committed nothing, the marker included");

        await AsTheFinish(() => SynchronizerService.Synchronize(loadout.LoadoutId));

        var paths = LoadoutPaths(Refresh(loadout));
        for (var i = 0; i < count; i++)
        {
            paths.Count(p => p == Written(i)).Should().Be(1, "each file the game wrote is in the loadout once");
            OnDisk(Written(i)).FileExists.Should().BeTrue();
            File.ReadAllText(OnDisk(Written(i)).GetFullPath()).Should().Be($"written by the game {i}");
        }
        OnDisk(Plugin).FileExists.Should().BeTrue();
        paths.Count(p => p == Plugin).Should().Be(1);
        ArchiveCount().Should().Be(archivesAfterTheFault, "the redo finds the first attempt's archive already in the store and writes no second one");
        for (var i = 0; i < count; i++)
            (await FileStore.HaveFile(FileIdentity.XxHash3(Encoding.UTF8.GetBytes($"written by the game {i}").xxHash3())))
                .Should().BeTrue("the file's bytes are in the store exactly where the first attempt put them");
        var metadata = GameInstallMetadata.Load(Connection.Db, MetadataId);
        metadata.Contains(GameInstallMetadata.LaunchFinishPending).Should().BeFalse("the redo's commit retracted the marker");
        metadata.Contains(GameInstallMetadata.LaunchFinishPendingSession).Should().BeFalse();
    }
}

/// <summary>
/// Hands every phase to the copy backend, except that its timestamp phase throws for one path on demand. The
/// throw is keyed on the path, not on a call count, so it fires on the same phase however the work is batched.
/// </summary>
public sealed class FaultingDeploymentBackend(CopyDeploymentBackend inner) : IDeploymentBackend
{
    public static readonly DeploymentBackendId BackendId = new("test-faulting");

    /// <summary>The path whose stamping fails, or null for no fault.</summary>
    public GamePath? FailStampingOf { get; set; }

    public DeploymentBackendId Id => BackendId;
    public bool UsesLooseStaging => false;
    public DeploymentPreflight Preflight(GameInstallation installation, GamePath root) => DeploymentPreflight.Accept("copies files");
    public IReadOnlyList<AbsolutePath> RequiredFilesystems(GameInstallation installation, GamePath root) => [];
    public Task Backup(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => inner.Backup(context, nodes);
    public void Ingest(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => inner.Ingest(context, nodes);
    public Task AdaptLoadout(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => inner.AdaptLoadout(context, nodes);
    public IReadOnlyCollection<GamePath> Delete(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => inner.Delete(context, nodes);
    public Task Extract(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => inner.Extract(context, nodes);

    public Task StampTimestamps(DeploymentContext context, IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)> stamps)
    {
        if (FailStampingOf is { } path && stamps.Any(s => s.Path == path))
            throw new IOException($"Injected fault while stamping {path}");
        return inner.StampTimestamps(context, stamps);
    }

    public Task WriteIntrinsics(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => inner.WriteIntrinsics(context, nodes);
    public void AddReifiedDeletes(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => inner.AddReifiedDeletes(context, nodes);

    public void CleanDirectories(DeploymentContext context, IReadOnlyCollection<GamePath> directoriesWithDeletions, Entities<DiskStateEntry.ReadOnly> newDiskState) =>
        inner.CleanDirectories(context, directoriesWithDeletions, newDiskState);
}

/// <summary>A game that names one plugin time, so every apply has a timestamp phase to fail in.</summary>
public class InterruptedIngestGame : IGame, IGameData<InterruptedIngestGame>, ITimestampOrderedGame
{
    public static GameId GameId { get; } = GameId.From("InterruptedIngestGame");
    public static string DisplayName => "Stubbed Game For Interrupted Ingest";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(uint.MaxValue - 7);

    public static List<(GamePath Path, DateTimeOffset Mtime)> Order { get; set; } = [];

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [50u],
        GOGProductIds = [50L],
        EGSCatalogItemId = ["epic-game-id-interrupted-ingest"],
    };

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<SilverModManager.StandardGameLocators.TestHelpers.StubbedGames.StubbedGame>("SilverModManager.StandardGameLocators.TestHelpers.Resources.question_mark_game.png");
    public IStreamFactory TileImage => throw new NotImplementedException("No game image for stubbed game.");

    private readonly IServiceProvider _serviceProvider;
    public InterruptedIngestGame(IServiceProvider provider) => _serviceProvider = provider;

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, "");

    public ILoadoutSynchronizer Synchronizer => new DefaultSynchronizer(_serviceProvider);

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) =>
        new Dictionary<LocationId, AbsolutePath>
        {
            { LocationId.Game, gameLocatorResult.Path.Combine("game") },
        }.ToImmutableDictionary();

    public ILibraryItemInstaller[] LibraryItemInstallers => [];
    public IDiagnosticEmitter[] DiagnosticEmitters => [];
    public ISortOrderManager SortOrderManager => new SortOrderManager(_serviceProvider);

    public Task<IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)>> PluginTimestamps(Loadout.ReadOnly loadout, IReadOnlyDictionary<GamePath, SyncNode> syncTree) =>
        Task.FromResult<IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)>>(Order);
}
