using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.HighPerformance.Buffers;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.GC;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.Abstractions.Loadouts.Files.Diff;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Sdk.GeneratedCaches;
using SilverModManager.Abstractions.Loadouts.Sorting;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.ForeignDeployments;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using Reloaded.Memory.Extensions;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

using DiskState = Entities<DiskStateEntry.ReadOnly>;

/// <summary>
/// Base class for loadout synchronizers, provides some common functionality. Does not have to be user,
/// but reduces a lot of boilerplate, and is highly recommended.
/// </summary>
public partial class ALoadoutSynchronizer : ILoadoutSynchronizer
{
    private readonly ScopedAsyncLock _lock = new();
    private readonly IFileStore _fileStore;

    protected readonly ILogger Logger;

    /// <summary>
    /// The lines that name one file each, on every pass. See <see cref="SynchronizerLogging.PerFileCategory"/>.
    /// </summary>
    private readonly ILogger _perFileLogger;
    private readonly ISorter _sorter;
    private readonly IGarbageCollectorRunner _garbageCollectorRunner;
    private readonly ISynchronizerService _synchronizerService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoadoutManager _loadoutManager;
    private readonly IGameLocationsService _gameLocationsService;
    private readonly IGameRegistry _gameRegistry;

    private readonly IReadOnlyDictionary<DeploymentBackendId, IDeploymentBackend> _backends;
    private readonly IDeploymentPlanStore _planStore;
    private readonly IDeploymentBackendSelector _backendSelector;
    private readonly ILooseStagingStore _stagingStore;

    private readonly StringPool _fileNamePool = new();

    /// <summary>
    /// Connection.
    /// </summary>
    protected readonly IConnection Connection;

    private readonly IJobMonitor _jobMonitor;
    private readonly IFileHashesService _fileHashService;

    /// <summary>Answers "same bytes?" across hash kinds from rows already recorded; never reads a file.</summary>
    protected readonly IFileIdentityResolver _identityResolver;
    private readonly IBackupOverflowPrompt _backupOverflowPrompt;
    private readonly BackupLimit _backupLimit;
    private readonly ArchiveVersionsAtApply _archiveVersionsAtApply;
    private readonly IMassRemovalPrompt _massRemovalPrompt;
    private readonly MassRemovalThreshold _massRemovalThreshold;
    private readonly IFullFileHasher _fullHasher;
    private readonly IForeignDeploymentSource[] _foreignDeploymentSources;
    private readonly GeneratedCacheClearer _cacheClearer;
    private readonly PendingChanges _pendingChanges;

    /// <summary>
    /// Loadout synchronizer base constructor.
    /// </summary>
    protected ALoadoutSynchronizer(
        IServiceProvider serviceProvider,
        ILogger logger,
        IFileStore fileStore,
        ISorter sorter,
        IConnection conn,
        IFileHashesService fileHashService,
        IGarbageCollectorRunner garbageCollectorRunner)
    {
        _serviceProvider = serviceProvider;
        _synchronizerService = serviceProvider.GetRequiredService<ISynchronizerService>();
        _jobMonitor = serviceProvider.GetRequiredService<IJobMonitor>();
        _loadoutManager = serviceProvider.GetRequiredService<ILoadoutManager>();
        _gameLocationsService = serviceProvider.GetRequiredService<IGameLocationsService>();
        _gameRegistry = serviceProvider.GetRequiredService<IGameRegistry>();

        // Resolved from DI rather than built here: games create a synchronizer per access.
        _backends = serviceProvider.GetServices<IDeploymentBackend>().ToDictionary(b => b.Id);
        _planStore = serviceProvider.GetRequiredService<IDeploymentPlanStore>();
        _backendSelector = serviceProvider.GetRequiredService<IDeploymentBackendSelector>();
        _stagingStore = serviceProvider.GetRequiredService<ILooseStagingStore>();
        _backupOverflowPrompt = serviceProvider.GetRequiredService<IBackupOverflowPrompt>();
        _backupLimit = serviceProvider.GetRequiredService<BackupLimit>();
        _archiveVersionsAtApply = serviceProvider.GetRequiredService<ArchiveVersionsAtApply>();
        _massRemovalPrompt = serviceProvider.GetRequiredService<IMassRemovalPrompt>();
        _massRemovalThreshold = serviceProvider.GetRequiredService<MassRemovalThreshold>();
        _fullHasher = serviceProvider.GetService<IFullFileHasher>() ?? MultiHashFileHasher.Instance;
        _foreignDeploymentSources = serviceProvider.GetServices<IForeignDeploymentSource>().ToArray();
        _cacheClearer = new GeneratedCacheClearer(serviceProvider.GetServices<IGeneratedCache>().ToArray(), logger);

        _fileHashService = fileHashService;
        _identityResolver = serviceProvider.GetRequiredService<IFileIdentityResolver>();

        Logger = logger;
        _perFileLogger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(SynchronizerLogging.PerFileCategory);
        _fileStore = fileStore;
        _sorter = sorter;
        Connection = conn;
        _garbageCollectorRunner = garbageCollectorRunner;
        _pendingChanges = new PendingChanges(this, Logger, _perFileLogger);
    }

    /// <summary>
    /// Helper constructor that takes only a service provider, and resolves the dependencies from it.
    /// </summary>
    /// <param name="provider"></param>
    protected ALoadoutSynchronizer(IServiceProvider provider) : this(
        provider,
        provider.GetRequiredService<ILogger<ALoadoutSynchronizer>>(),
        provider.GetRequiredService<IFileStore>(),
        provider.GetRequiredService<ISorter>(),
        provider.GetRequiredService<IConnection>(),
        provider.GetRequiredService<IFileHashesService>(),
        provider.GetRequiredService<IGarbageCollectorRunner>()
    ) { }

    /// <summary>
    /// Gets or creates the override group.
    /// </summary>
    public LoadoutOverridesGroupId GetOrCreateOverridesGroup(ITransaction tx, Loadout.ReadOnly loadout)
    {
        if (LoadoutOverridesGroup.FindByOverridesFor(loadout.Db, loadout.Id).TryGetFirst(out var found))
            return found;

        var newOverrides = new LoadoutOverridesGroup.New(tx, out var id)
        {
            OverridesForId = loadout,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id)
                {
                    Name = "Overrides",
                    LoadoutId = loadout.Id,
                },
            },
        };

        return newOverrides.Id;
    }

    /// <summary>
    /// The install's row, which the rescan indexes and the apply writes. A loadout on any other row for the same
    /// folder is refused: its sync would read one row's disk state and record the result on another.
    /// </summary>
    private GameInstallMetadata.ReadOnly MetadataOf(Loadout.ReadOnly loadout)
    {
        var installation = loadout.InstallationInstance;
        if (!_gameRegistry.TryGetMetadata(installation, out var metadata))
        {
            throw new SynchronizerPreflightException(
                $"Loadout `{loadout.Name}` is recorded against the {loadout.Game.DisplayName} install at `{loadout.Installation.Path}`, " +
                $"but no install record matches the folder found at `{installation.LocatorResult.Path}`. Nothing was changed.");
        }
        if (metadata.GameInstallMetadataId == loadout.InstallationId) return metadata;

        var second = loadout.Installation.Path == metadata.Path
            ? $"a second record of the {loadout.Game.DisplayName} install at `{metadata.Path}`"
            : $"a second record of the {loadout.Game.DisplayName} install: its record names `{loadout.Installation.Path}` " +
              $"and the install's own record names `{metadata.Path}`, which are the same folder";
        throw new SynchronizerPreflightException(
            $"Loadout `{loadout.Name}` is recorded against {second}, so applying it would mix up which files are tracked. Nothing was changed.");
    }

    /// <summary>
    /// Every path that writes a loadout into the game folder or reads the folder back into it comes through
    /// <see cref="Synchronize"/>, so the check lives here. Optional so a host without installs syncs as before.
    /// </summary>
    /// <exception cref="LoadoutInstallRunningException">An install is still writing into <paramref name="loadout"/>.</exception>
    private void EnsureNoInstallInto(LoadoutId loadout)
    {
        if (_serviceProvider.GetService<ILoadoutInstallActivity>() is { } activity && activity.IsInstallingInto(loadout))
            throw activity.BlockedApply(loadout);
    }

    public record struct AddedEntry
    {
        public required LoadoutItem.New LoadoutItem { get; init; }
        public required LoadoutItemWithTargetPath.New LoadoutItemWithTargetPath { get; init; }
        public required LoadoutFile.New LoadoutFileEntry { get; init; }
    }

    /// <inheritdoc />
    public virtual async Task<Loadout.ReadOnly> Synchronize(Loadout.ReadOnly loadout, SynchronizeLoadoutJob? job = null, BackupOverflowChoice? largeFiles = null, CancellationToken cancellationToken = default)
    {
        // Refuse before the locator ids are updated and the game folder is re-indexed: both write to the
        // database, and neither can mean anything when the prefix they read from is gone.
        await Preflight(loadout.InstallationInstance);
        ThrowIfMounted(loadout.InstallationInstance);
        _ = MetadataOf(loadout);
        EnsureNoInstallInto(loadout.LoadoutId);

        // A switch syncs the loadout on disk first, and that one can be the one a list is being installed into.
        if (GameInstallMetadata.LastSyncedLoadout.TryGetValue(loadout.Installation, out var onDiskId))
            EnsureNoInstallInto(LoadoutId.From(onDiskId));

        loadout = loadout.Rebase();
        SyncOrigin.LogStart(Logger, $"sync of loadout {loadout.Name} ({loadout.LoadoutId}) at revision {loadout.Db.BasisTxId}");

        // After the rebase so a game judges the loadout as it is now, not the snapshot the caller held.
        await PreflightLoadout(loadout);
        
        // Update locator IDs before building the sync tree
        (loadout, var installation) = await UpdateLocatorIds(loadout, cancellationToken);
        
        // One answer to the backup-cap question covers every step of this apply, a switch's three included.
        using var answer = BackupOverflowAnswer.Begin(BackupOverflowOperation.Apply);

        // If we are swapping loadouts, then we need to synchronize the previous loadout first to ingest
        // any changes, then we can apply the new loadout.
        if (Sdk.Games.GameInstallMetadata.LastSyncedLoadout.TryGetValue(loadout.Installation, out var lastAppliedId) && lastAppliedId != loadout.Id)
        {
            var prevLoadout = Loadout.Load(loadout.Db, lastAppliedId);
            if (prevLoadout.IsValid())
            {
                await _loadoutManager.DeactivateCurrentLoadout(loadout.InstallationInstance, cancellationToken, largeFiles);
                await _loadoutManager.ActivateLoadout(loadout, cancellationToken, largeFiles);
                return loadout.Rebase();
            }
        }

        job?.SetStatus(SyncPhases.Collecting);

        // Folders that differ only in case are merged and the recorded paths respelled before anything reads them, so the
        // game-installed rows below record the spelling the disk has.
        var metadata = await FoldAndReindex(installation, loadout.Id, job, alwaysReindex: true, cancellationToken);
        loadout = loadout.Rebase();

        try
        {
            // Reconcile sees the fresh disk state, and the game's preparation sees the group it writes.
            loadout = await ReconcileGameInstalledContent(loadout, metadata, cancellationToken);
            loadout = await PrepareLoadoutForSync(loadout);

            // Rows already in the Overrides are settled before the tree is built, so the apply covers them: an edit made
            // after the apply would read as a change still to apply.
            loadout = await ReprocessOverrides(loadout, recordAsApplied: false);

            // The preparation can respell recorded disk state, and the tree reads the state it left, not the snapshot taken before.
            metadata = GameInstallMetadata.Load(Connection.Db, metadata.Id);

            var tree = BuildSyncTreeFromIndex(loadout, metadata);
            ProcessSyncTree(tree);

            var (choice, asked) = await ResolveBackupOverflow(tree, loadout.InstallationInstance, largeFiles, cancellationToken, job: job);
            if (asked)
            {
                // The folder can change while the question is open, so the answer is applied to a tree of the folder as
                // it is now. The answer is the preset this time, so nothing is asked twice.
                metadata = await ReindexState(installation, job, cancellationToken);
                tree = BuildSyncTreeFromIndex(loadout, metadata);
                ProcessSyncTree(tree);
                (choice, _) = await ResolveBackupOverflow(tree, loadout.InstallationInstance, choice, cancellationToken, presetIsAnAnswer: true, job: job);
            }

            await ConfirmMassRemoval(tree, loadout, cancellationToken);

            loadout = await RunActions(tree, loadout, job, choice);

            // The apply can ingest a file whose path is a game file, which is dropped again here and never deployed.
            // That edit follows the record of what was applied, so the record moves with it.
            return await ReprocessOverrides(loadout, recordAsApplied: true);
        }
        finally
        {
            _applySpellings = null;
        }
    }

    /// <summary>
    /// Returns true if the loadout state doesn't match the last scanned disk state. A generated file with nothing
    /// else pending is rendered when it declares <see cref="IIntrinsicFileRenderedFromLoadout"/>, and is work only if
    /// the bytes differ from what the last apply recorded for it; any other generated file is always work.
    /// </summary>
    public async ValueTask<bool> ShouldSynchronize(Loadout.ReadOnly loadout, IEnumerable<PathPartPair> previousDiskState, IEnumerable<PathPartPair> lastScannedDiskState)
        => (await EvaluateSynchronize(loadout, previousDiskState, lastScannedDiskState)).Needed;

    /// <inheritdoc />
    public ValueTask<SynchronizeEvaluation> EvaluateSynchronize(Loadout.ReadOnly loadout, IEnumerable<PathPartPair> previousDiskState, IEnumerable<PathPartPair> lastScannedDiskState)
        => _pendingChanges.Evaluate(loadout, previousDiskState, lastScannedDiskState);

    /// <inheritdoc />
    public FileDiffTree LoadoutToDiskDiff(Loadout.ReadOnly loadout, List<PathPartPair> previousDiskState, List<PathPartPair> lastScannedDiskState)
        => _pendingChanges.DiskDiff(loadout, previousDiskState, lastScannedDiskState);

    /// <summary>
    /// Runs before a sync tree is built for an Apply or a loadout switch, so a game can bring the loadout up to
    /// date first (for example by seeding data from a game file). It may commit to the database and must return
    /// the loadout at the revision the tree should be built on. It is not called from the public
    /// <c>BuildSyncTree(loadout, job)</c>, which diagnostics and the CLI use and which must never write.
    /// </summary>
    protected virtual ValueTask<Loadout.ReadOnly> PrepareLoadoutForSync(Loadout.ReadOnly loadout) => ValueTask.FromResult(loadout);

    /// <inheritdoc />
    public virtual bool IsIgnoredBackupPath(GamePath path) => false;

    /// <inheritdoc />
    public virtual bool CanMoveOverrides(Loadout.ReadOnly loadout, GamePath path) => true;

    /// <inheritdoc />
    public virtual bool IsGameWrittenPath(GamePath path) => GameWrittenFiles.IsLogOrCrashFile(path);

    private IGamePathFilter? _composedFilter;

    /// <summary>
    /// Whether the path is a record another mod manager (Vortex's deployment manifests and folder markers) keeps in the
    /// game folder. They stay on disk and are never indexed, so they are not ingested as a mod file, backed up or
    /// deleted when a mod is switched off. A folder a mod keeps its generated cache in is filtered the same way
    /// (see <see cref="IGeneratedCache"/>); only the cache clearing deletes from it.
    /// </summary>
    private bool IsForeignManagerRecord(GamePath path)
    {
        foreach (var source in _foreignDeploymentSources)
        {
            if (source.IsRecordFile(path)) return true;
        }

        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The base rule joined with <see cref="GameSpecificPathFilter"/>. It is composed on first use rather than in
    /// an initializer because an initializer cannot call the virtual member.
    /// </remarks>
    public IGamePathFilter GamePathFilter => _composedFilter ??= Synchronizers.GamePathFilters.Any(BaseGamePathFilter, GameSpecificPathFilter, Synchronizers.GamePathFilters.Create(path => IsForeignManagerRecord(path) || _cacheClearer.IsOwnedPath(path)));

    /// <summary>
    /// The rule every game gets: save files are never indexed, backed up, overwritten or deleted, whichever
    /// folder the game's <see cref="LocationId.Saves"/> location resolves to. A game's own rules cover only the
    /// save folders its author knew about, and a reset or unmanage would otherwise delete the rest.
    /// </summary>
    /// <remarks>
    /// Proton's shared shader caches are filtered too, see <see cref="IsSharedProtonCache"/>, and so are source and project
    /// files no game loads (see <see cref="NeverLoadedFiles"/>): a mod author's <c>src/</c> folder left in the game folder
    /// is neither ingested nor reported as an external change. A copy an older build wrote is
    /// still removed with its mod: its disk state entry keeps it indexed (see <see cref="IsTrackedProtectedFile"/>), and
    /// the last applied state is what marks it as written by the app, so the delete is safe.
    /// </remarks>
    private static readonly IGamePathFilter BaseGamePathFilter = Synchronizers.GamePathFilters.Create(path => path.LocationId == LocationId.Saves || IsSharedProtonCache(path) || IsWriteProbe(path) || NeverLoadedFiles.IsNeverLoaded(path));

    /// <summary>
    /// Whether to ignore the file at the given path when indexing, in addition to the rule every game gets.
    /// </summary>
    /// <remarks>
    /// Files ignored by this method will not be included in the sync tree. Prefer not including
    /// the path in the first place instead of using this method.
    /// </remarks>
    protected virtual IGamePathFilter GameSpecificPathFilter { get; } = Synchronizers.GamePathFilters.Empty;

    /// <summary>
    /// Gets a set of files intrinsic to this game. Such as mod order files, preference files, etc.
    /// These files will not be backed up and will not be included in the loadout directly. Instead, they are
    /// generated at sync time by calling the implementations of the files themselves. 
    /// </summary>
    public virtual Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout)
    {
        return new();
    }

    /// <inheritdoc />
    public void EnsureBuildCovered(GameInstallation installation, LocatorId[] locatorIds)
    {
        if (_fileHashService.CoversInstalledBuild((installation.LocatorResult.Store, locatorIds))) return;

        throw new SynchronizerPreflightException(
            $"{installation.Game.DisplayName}'s installed build is not in the app's game file data yet, so the app cannot tell the game's own files from added ones and will not clean the folder. Existing files are kept as External Changes.");
    }
}
