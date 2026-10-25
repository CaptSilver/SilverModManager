using SilverModManager.Sdk.Loadouts;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Native;
using SilverModManager.Sdk.Settings;
using SilverModManager.Deployment.Staging;

namespace SilverModManager.Deployment.Links.Tests.Fixtures;

/// <summary>
/// The filesystem a folder is created on. Placement decides which link backend works, so tests name it.
/// </summary>
public enum Placement
{
    /// <summary>The temp directory as it was before test.sh moved TMPDIR, tmpfs on the dev box: hard links work, FICLONE does not.</summary>
    TempDirectory,

    /// <summary>The reflink scratch root, or beside the test binaries without one (btrfs on the dev box, where FICLONE works).</summary>
    EntryDirectory,

    /// <summary>/dev/shm, a different device than the temp directory.</summary>
    SharedMemory,
}

/// <summary>
/// A stubbed game whose folders and staging store sit where the test says, with the link backends registered
/// and the selection pinned to the backend under test (or left to the real chain when none is named).
/// </summary>
public abstract class LinkedGameTest<TTest>(ITestOutputHelper helper, Placement game, Placement store, DeploymentBackendId? pinned)
    : AStubbedGameIsolatedTest<TTest>(helper)
{
    private static readonly ConcurrentBag<string> CreatedDirectories = [];

    static LinkedGameTest()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var path in CreatedDirectories)
            {
                try { Directory.Delete(path, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        };
    }

    // Initialised before the base constructor builds the service graph, like the captured parameters above.
    protected readonly LogCapture Logs = new();

    protected GameInstallMetadataId MetadataId { get; private set; }

    protected ILooseStagingStore Store => ServiceProvider.GetRequiredService<ILooseStagingStore>();

    protected IDeploymentPlanStore Plans => ServiceProvider.GetRequiredService<IDeploymentPlanStore>();

    /// <summary>
    /// Records the backend for the game folder directly, without the preflight a move would run.
    /// </summary>
    protected async Task RecordGameRoot(DeploymentBackendId backend)
    {
        using var tx = Connection.BeginTransaction();
        Plans.Write(tx, Connection.Db, MetadataId, GamePathOf(""), backend, chosen: true);
        await tx.Commit();
    }

    protected T Backend<T>() where T : IDeploymentBackend => ServiceProvider.GetServices<IDeploymentBackend>().OfType<T>().Single();

    protected override AbsolutePath TemporaryFilesPrefix => PlacementDirectory(game);

    protected override AbsolutePath StagingStoreLocation => PlacementDirectory(store);

    /// <summary>
    /// Switches the staging store's per-filesystem stores on. Off by default: a store at the root of a real
    /// /dev/shm or second disk is not a temp directory.
    /// </summary>
    protected virtual bool PerFilesystemStores => false;

    /// <summary>
    /// Stands in for the filesystem lookup, so a test can say where another filesystem's store goes.
    /// </summary>
    protected virtual IFilesystemLocator? FilesystemLocator => null;

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        if (FilesystemLocator is { } locator) services.AddSingleton(locator);

        // Registered first: the data model adds the real chain with TryAdd.
        if (pinned is { } id) services.AddSingleton<IDeploymentBackendSelector>(new PinnedSelector(id));
        services.AddSingleton<ILoggerProvider>(Logs);
        base.AddServices(services).AddLinkDeploymentBackends();

        // After the base graph: overrides apply in registration order, and its own turns the setting off.
        if (PerFilesystemStores) services.OverrideSettingsForTests<LooseStagingStoreSettings>(settings => settings with { PerFilesystemStores = true });
        return services;
    }

    protected static AbsolutePath PlacementDirectory(Placement placement)
    {
        var parent = placement switch
        {
            Placement.TempDirectory => TestScratch.HostTemp,
            Placement.SharedMemory => "/dev/shm",
            _ => TestScratch.ReflinkRoot,
        };

        var path = Path.Combine(parent, $"links-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        CreatedDirectories.Add(path);
        return NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(path);
    }

    /// <summary>
    /// Skips the test when a placement is not available on this machine, then manages the installation and
    /// creates a loadout.
    /// </summary>
    protected async Task<Loadout.ReadOnly> NewLoadout()
    {
        foreach (var placement in new[] { game, store })
        {
            switch (placement)
            {
                case Placement.SharedMemory:
                    Assert.SkipUnless(Directory.Exists("/dev/shm"), "/dev/shm does not exist");
                    Assert.Equal(0, LibcStat.TryStat("/dev/shm", out var shm));
                    Assert.Equal(0, LibcStat.TryStat(TestScratch.HostTemp, out var temp));
                    Assert.SkipWhen(shm.Device == temp.Device, "/dev/shm is on the same device as the temp directory");
                    break;
                case Placement.EntryDirectory:
                    Assert.SkipUnless(CanReflink(TestScratch.ReflinkRoot), $"FICLONE is not supported on the reflink scratch filesystem ({TestScratch.ReflinkRoot})");
                    break;
            }
        }

        var metadata = await LoadoutManager.ManageInstallation(GameInstallation);
        MetadataId = metadata.Id;
        return await CreateLoadout();
    }

    private static bool CanReflink(string directory)
    {
        var source = Path.Combine(directory, $"clone-source-{Guid.NewGuid():N}");
        var destination = Path.Combine(directory, $"clone-destination-{Guid.NewGuid():N}");
        File.WriteAllText(source, "x");
        try
        {
            using var from = File.OpenHandle(source);
            using var to = File.OpenHandle(destination, FileMode.Create, FileAccess.Write);
            return Native.LibcLinks.TryClone(from, to) == 0;
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }

    /// <summary>
    /// Adds a mod with the given files to the game folder and archives their contents. Returns the mod's
    /// group, for enabling and disabling, and each file's hash.
    /// </summary>
    protected async Task<(EntityId Group, Dictionary<string, Hash> Hashes)> AddMod(Loadout.ReadOnly loadout, string name, params (string Path, string Content)[] files)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        var archived = new List<ArchivedFileEntry>();
        var hashes = new Dictionary<string, Hash>();
        foreach (var (path, content) in files)
        {
            AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, path), content, out var hash, out var size);
            archived.Add(new ArchivedFileEntry(new MemoryStreamFactory(path, new MemoryStream(Encoding.UTF8.GetBytes(content))), hash, size));
            hashes[path] = hash;
        }
        await FileStore.BackupFiles(archived);
        var result = await tx.Commit();
        return (result[group.Value], hashes);
    }

    protected GamePath GamePathOf(string path) => new(LocationId.Game, path);

    protected AbsolutePath OnDisk(string path) => GameInstallation.Locations.ToAbsolutePath(GamePathOf(path));

    protected static LibcStat.Buffer Stat(AbsolutePath path)
    {
        Assert.Equal(0, LibcStat.TryStat(path.ToString(), out var stat));
        return stat;
    }

    protected static bool IsSymlink(AbsolutePath path) => LibcStat.TryLStat(path.ToString(), out var stat) == 0 && (stat.Mode & LibcStat.TypeMask) == LibcStat.Symlink;

    /// <summary>
    /// The sync tree for what is on disk now, as a synchronize would build it.
    /// </summary>
    protected async Task<Dictionary<GamePath, SyncNode>> BuildTree(Loadout.ReadOnly loadout)
    {
        var tree = await Synchronizer.BuildSyncTree(loadout);
        Synchronizer.ProcessSyncTree(tree);
        return tree;
    }

    protected DeploymentContext ContextFor(Dictionary<GamePath, SyncNode> tree, ITransaction tx, Loadout.ReadOnly? loadout, ALoadoutSynchronizer? synchronizer = null) => new()
    {
        Synchronizer = synchronizer ?? (ALoadoutSynchronizer)Synchronizer,
        Installation = GameInstallation,
        MetadataId = MetadataId,
        SyncTree = tree,
        Loadout = loadout,
        Transaction = tx,
    };

    /// <summary>
    /// Whether the game path has a disk state row, as of the latest database.
    /// </summary>
    protected bool HasDiskState(string path)
    {
        var wanted = GamePathOf(path);
        return DiskStateEntry.FindByGame(Connection.Db, MetadataId).Any(e => (GamePath)e.Path == wanted);
    }

    protected sealed class PinnedSelector(DeploymentBackendId id) : IDeploymentBackendSelector
    {
        // Only the game folder is pinned: the other locations stay with copy, so a test that moves the game
        // to another device is not also refused for the preferences folder.
        public DeploymentChoice Choose(GameInstallation installation, GamePath root) =>
            root.LocationId == LocationId.Game ? new(id, "pinned by the test") : new(DeploymentBackendId.Copy, "only the game folder is pinned");
    }

    /// <summary>
    /// Collects warnings and above so a test can see what a backend complained about.
    /// </summary>
    protected sealed class LogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _warnings = new();

        public IReadOnlyCollection<string> Warnings => _warnings;

        public ILogger CreateLogger(string categoryName) => new Capture(_warnings);

        public void Dispose() { }

        private sealed class Capture(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) sink.Enqueue(formatter(state, exception));
            }
        }
    }
}
