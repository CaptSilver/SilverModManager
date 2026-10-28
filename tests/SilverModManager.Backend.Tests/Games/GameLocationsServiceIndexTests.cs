using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Steam.DTOs;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.Games.FileHashes;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Backend.Games;
using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Storage;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.NexusModsApi;
using NSubstitute;
using Connection = SilverModManager.MnemonicDB.Connection;

namespace SilverModManager.Backend.Tests.Games;

public sealed class GameLocationsServiceIndexTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly List<IDisposable> _owned = [];
    private readonly TemporaryFileManager _temp = new(FileSystem.Shared);
    private readonly CountingHasher _hasher = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly AbsolutePath _gameDir;
    private readonly GameInstallation _installation;
    private readonly IConnection _connection;
    private readonly EntityId _metadataId;

    public GameLocationsServiceIndexTests()
    {
        _provider = new ServiceCollection()
            .AddLogging()
            .AddMnemonicDB()
            .AddDiskStateEntryModel()
            .AddGameInstallMetadataModel()
            .AddHashRelationModel()
            .AddPathHashRelationModel()
            .AddLocalFileHashModel()
            .AddLocalBaselineModel()
            .AddNonVanillaFileModel()
            .AddSteamManifestModel()
            .AddVersionDefinitionModel()
            .BuildServiceProvider();

        var backend = new MnemonicDB.Storage.RocksDbBackend.Backend();
        var store = new DatomStore(_provider.GetRequiredService<ILogger<DatomStore>>(), DatomStoreSettings.InMemory, backend);
        _connection = new Connection(_provider.GetRequiredService<ILogger<Connection>>(), store, _provider, []);
        _owned.Add(backend);
        _owned.Add(store);
        _owned.Add((IDisposable)_connection);

        _metadataId = EntityId.From(PartitionId.User(9).Value);
        _gameDir = _temp.CreateFolder().Path;
        var game = Substitute.For<IGame>();
        game.DisplayName.Returns("Fixture Game");
        var locatorResult = new GameLocatorResult
        {
            Game = game,
            Path = _gameDir,
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = [],
            Locator = Substitute.For<IGameLocator>(),
        };
        _installation = new GameInstallation(locatorResult, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, _gameDir)));
    }

    public void Dispose()
    {
        _temp.Dispose();
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _provider.Dispose();
    }

    [Fact]
    public async Task FirstIndex_NeverReadsMoreFilesAtOnceThanTheSharedCap()
    {
        // On a machine with no more threads than the cap the unbounded loop never exceeds it, so nothing could fail.
        if (Environment.ProcessorCount <= HashingLimits.FileReaders) return;
        await WriteGameFiles(64);
        var service = CreateService();

        await service.IndexGame(_installation, FrozenDictionary<GamePath, DiskStateEntry.ReadOnly>.Empty, GamePathFilters.Empty);

        _hasher.PeakConcurrency.Should().BeLessThanOrEqualTo(HashingLimits.FileReaders);
    }

    [Fact]
    public async Task ReindexAfterAnUnchangedIndex_HashesNothing()
    {
        await WriteGameFiles(20);
        var service = CreateService();

        var first = await service.IndexGame(_installation, FrozenDictionary<GamePath, DiskStateEntry.ReadOnly>.Empty, GamePathFilters.Empty);
        first.NewFiles.Should().HaveCount(20);
        _hasher.Calls.Should().Be(20);
        var diskState = await Record(first);
        _hasher.Reset();

        var second = await service.IndexGame(_installation, diskState, GamePathFilters.Empty);

        second.NewFiles.Should().BeEmpty();
        second.ModifiedFiles.Should().BeEmpty();
        second.RemovedFiles.Should().BeEmpty();
        _hasher.Calls.Should().Be(0);
    }

    [Fact]
    public async Task FirstIndex_OfFilesTheStoreHasRowsFor_ReadsNoneInFull()
    {
        await WriteGameFiles(20);
        var store = await StoreWithRowsFor(20);
        var service = CreateService(stores: [store]);

        var result = await service.IndexGame(_installation, FrozenDictionary<GamePath, DiskStateEntry.ReadOnly>.Empty, GamePathFilters.Empty);

        result.NewFiles.Should().HaveCount(20);
        _hasher.Calls.Should().Be(0);
        result.NewFiles[new GamePath(LocationId.Game, "data/file4.bin")].Hash.Should().Be((await MultiHasher.HashStream(new MemoryStream(Content(4)))).XxHash3);
    }

    [Fact]
    public async Task ChangedFile_IsHashedExactlyOnce()
    {
        await WriteGameFiles(20);
        var service = CreateService();
        var diskState = await Record(await service.IndexGame(_installation, FrozenDictionary<GamePath, DiskStateEntry.ReadOnly>.Empty, GamePathFilters.Empty));
        _hasher.Reset();

        var changed = _gameDir.Combine("data/file3.bin");
        await changed.WriteAllBytesAsync(Content(3, extra: 1));

        var second = await service.IndexGame(_installation, diskState, GamePathFilters.Empty);

        second.ModifiedFiles.Keys.Select(k => k.Path.ToString()).Should().BeEquivalentTo(["data/file3.bin"]);
        _hasher.Calls.Should().Be(1);
        _hasher.Paths.Should().ContainSingle().Which.Should().Be(changed);
    }

    [Fact]
    public async Task FileRewrittenWithTheSameSizeAndANewerTime_IsHashedExactlyOnce()
    {
        await WriteGameFiles(20);
        var service = CreateService();
        var diskState = await Record(await service.IndexGame(_installation, FrozenDictionary<GamePath, DiskStateEntry.ReadOnly>.Empty, GamePathFilters.Empty));
        _hasher.Reset();

        var changed = _gameDir.Combine("data/file7.bin");
        var rewritten = Content(7);
        rewritten[0] ^= 0xFF;
        await changed.WriteAllBytesAsync(rewritten);
        File.SetLastWriteTimeUtc(changed.GetFullPath(), DateTime.UtcNow.AddMinutes(5));

        var second = await service.IndexGame(_installation, diskState, GamePathFilters.Empty);

        second.ModifiedFiles.Keys.Select(k => k.Path.ToString()).Should().BeEquivalentTo(["data/file7.bin"]);
        _hasher.Paths.Should().ContainSingle().Which.Should().Be(changed);
    }

    [Fact]
    public async Task IndexGame_WaitsForABaselineBuildInFlight_BeforeReadingAnyFile()
    {
        await WriteGameFiles(5);
        var build = new TaskCompletionSource();
        var service = CreateService(waitForBuild: build.Task);

        var index = service.IndexGame(_installation, FrozenDictionary<GamePath, DiskStateEntry.ReadOnly>.Empty, GamePathFilters.Empty);
        await Task.Delay(200);

        index.IsCompleted.Should().BeFalse("the rows the build is about to commit may cover these files");
        _hasher.Calls.Should().Be(0);

        build.SetResult();
        (await index).NewFiles.Should().HaveCount(5);
    }

    [Fact]
    public async Task SecondIndexWithEmptyDiskState_DoesNotRehashAnUnchangedFile()
    {
        await WriteGameFiles(5);
        var service = CreateService();
        await service.IndexGame(_installation, FrozenDictionary<GamePath, DiskStateEntry.ReadOnly>.Empty, GamePathFilters.Empty);
        _hasher.Calls.Should().Be(5);
        _hasher.Reset();

        var second = await service.IndexGame(_installation, FrozenDictionary<GamePath, DiskStateEntry.ReadOnly>.Empty, GamePathFilters.Empty);

        second.NewFiles.Should().HaveCount(5);
        _hasher.Calls.Should().Be(0);
        second.NewFiles[new GamePath(LocationId.Game, "data/file2.bin")].Hash.Should().Be((await MultiHasher.HashStream(new MemoryStream(Content(2)))).XxHash3);
    }

    [Fact]
    public async Task TouchedFile_IsRehashedDespiteItsFingerprintRow()
    {
        await WriteGameFiles(3);
        var service = CreateService();
        await service.IndexGame(_installation, FrozenDictionary<GamePath, DiskStateEntry.ReadOnly>.Empty, GamePathFilters.Empty);
        _hasher.Reset();
        File.SetLastWriteTimeUtc(_gameDir.Combine("data/file1.bin").GetFullPath(), DateTime.UtcNow.AddMinutes(1));

        await service.IndexGame(_installation, FrozenDictionary<GamePath, DiskStateEntry.ReadOnly>.Empty, GamePathFilters.Empty);

        _hasher.Paths.Should().ContainSingle().Which.Should().Be(_gameDir.Combine("data/file1.bin"));
    }

    [Fact]
    public async Task PathsDifferingOnlyInCase_GetTheirOwnRows()
    {
        // The index keys files by GamePath, which ignores case, so only one of two such files reaches the row writer
        // per index. The rows must still keep them apart: they come from the case-insensitive path index and a lookup
        // that ignored the spelling would hand one file the other's hash.
        var hashes = new LocalRowsFileHashes(_connection, _gameDir.ToString(), [], Task.CompletedTask);
        var upper = new GamePath(LocationId.Game, "Mods/x.bin");
        var lower = new GamePath(LocationId.Game, "mods/x.bin");
        var size = Size.From(10);
        var stamp = DateTimeOffset.UtcNow.AddHours(-1);
        var upperHash = Hash.From(1);
        var lowerHash = Hash.From(2);

        await hashes.RecordIndexedFiles(_installation, [new IndexedFileHash(upper, size, stamp, upperHash)]);
        await hashes.RecordIndexedFiles(_installation, [new IndexedFileHash(lower, size, stamp, lowerHash)]);

        hashes.TryGetFingerprintedHash(_installation, upper, size, stamp, out var foundUpper).Should().BeTrue();
        hashes.TryGetFingerprintedHash(_installation, lower, size, stamp, out var foundLower).Should().BeTrue();
        foundUpper.Should().Be(upperHash);
        foundLower.Should().Be(lowerHash);
        LocalRows().Select(r => r.Path.Path).Should().BeEquivalentTo(["Mods/x.bin", "mods/x.bin"]);
    }

    [Fact]
    public async Task FileRewrittenWhileItIsRead_GetsNoFingerprintRow()
    {
        await WriteGameFiles(2);
        var moving = _gameDir.Combine("data/file0.bin");
        var tick = 0;
        _hasher.OnHash = file =>
        {
            if (file != moving) return;
            File.SetLastWriteTimeUtc(file.GetFullPath(), DateTime.UtcNow.AddMinutes(10 + ++tick));
        };
        var service = CreateService();

        await service.IndexGame(_installation, FrozenDictionary<GamePath, DiskStateEntry.ReadOnly>.Empty, GamePathFilters.Empty);

        LocalRows().Select(r => r.Path.Path).Should().BeEquivalentTo(["data/file1.bin"]);
        _hasher.OnHash = null;
        _hasher.Reset();

        await service.IndexGame(_installation, FrozenDictionary<GamePath, DiskStateEntry.ReadOnly>.Empty, GamePathFilters.Empty);

        _hasher.Paths.Should().ContainSingle().Which.Should().Be(moving);
    }

    [Fact]
    public async Task Index_AfterABaseline_TakesEveryReadVanillaFileFromItsFingerprint()
    {
        await WriteGameFiles(5);
        var manifest = new Manifest
        {
            ManifestId = ManifestId.From(1),
            DepotId = DepotId.From(2),
            CreationTime = DateTimeOffset.UnixEpoch,
            TotalCompressedSize = Size.Zero,
            TotalUncompressedSize = Size.Zero,
            Files = await Task.WhenAll(Enumerable.Range(0, 5).Select(async i => new Manifest.FileData
            {
                Path = RelativePath.FromUnsanitizedInput($"data/file{i}.bin"),
                Size = Size.FromLong(Content(i).Length),
                Hash = (await MultiHasher.HashStream(new MemoryStream(Content(i)))).Sha1,
                Chunks = [],
            })),
        };
        // No store trust, so the baseline reads all five itself.
        await new LocalVanillaBaseline(FileSystem.Shared, NullLogger<LocalVanillaBaseline>.Instance).Build(new BaselineRequest
        {
            GameRoot = _gameDir,
            Store = GameStore.Steam,
            AppId = AppId.From(1),
            GamePath = _gameDir.ToString(),
            LocatorIds = ["1"],
            Manifests = [manifest],
            AllInstalledDepotsCovered = true,
            IsIgnored = _ => false,
            Game = GameId.From("IndexFixture"),
            NexusGameId = NexusModsGameId.From(1234),
            OperatingSystem = SilverModManager.Abstractions.Games.FileHashes.Values.OperatingSystem.Windows,
        }, _connection);
        var service = CreateService();

        var result = await service.IndexGame(_installation, FrozenDictionary<GamePath, DiskStateEntry.ReadOnly>.Empty, GamePathFilters.Empty);

        result.NewFiles.Should().HaveCount(5);
        _hasher.Calls.Should().Be(0);
        // The baseline's path rows would also satisfy the minimal-hash lookup without a full read, so the
        // counts say which route each hash took.
        _logs.Value("FromFingerprint").Should().Be(5L);
        _logs.Value("FromStored").Should().Be(0L);
    }

    private IReadOnlyList<LocalFileHash.ReadOnly> LocalRows() => LocalFileHash.All(_connection.Db).ToList();

    private async Task WriteGameFiles(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var path = _gameDir.Combine($"data/file{i}.bin");
            path.Parent.CreateDirectory();
            await path.WriteAllBytesAsync(Content(i));
        }
    }

    // Larger than the 128 KiB a minimal hash covers in full, so a stored hash cannot be confused with a whole-file read.
    private static byte[] Content(int seed, int extra = 0)
    {
        var bytes = new byte[300 * 1024 + extra];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private async Task<FrozenDictionary<GamePath, DiskStateEntry.ReadOnly>> Record(IndexGameResult result)
    {
        using var tx = _connection.BeginTransaction();
        foreach (var (gamePath, file) in result.NewFiles)
        {
            _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
            {
                Path = gamePath.ToGamePathParentTuple(_metadataId),
                Hash = file.Hash,
                Size = file.Size,
                LastModified = file.LastModified,
                GameId = _metadataId,
            };
        }
        var committed = await tx.Commit();
        return DiskStateEntry.FindByGame(committed.Db, _metadataId).ToFrozenDictionary(e => (GamePath)e.Path);
    }

    private async Task<IDb> StoreWithRowsFor(int count)
    {
        using var tx = _connection.BeginTransaction();
        for (var i = 0; i < count; i++)
        {
            var hashes = await MultiHasher.HashStream(new MemoryStream(Content(i)));
            var hash = new HashRelation.New(tx)
            {
                MinimalHash = hashes.MinimalHash,
                XxHash3 = hashes.XxHash3,
                Sha1 = hashes.Sha1,
                XxHash64 = hashes.XxHash64,
                Md5 = hashes.Md5,
                Size = hashes.Size,
                Crc32 = hashes.Crc32,
            };
            _ = new PathHashRelation.New(tx) { Path = RelativePath.FromUnsanitizedInput($"data/file{i}.bin"), HashId = hash };
        }
        return (await tx.Commit()).Db;
    }

    private GameLocationsService CreateService(Task? waitForBuild = null, IDb[]? stores = null)
    {
        var hashes = new LocalRowsFileHashes(_connection, _gameDir.ToString(), stores ?? [], waitForBuild ?? Task.CompletedTask);

        var services = new ServiceCollection()
            .AddLogging(b => b.AddProvider(_logs))
            .AddSingleton<IFileHashesService>(hashes)
            .AddSingleton<IFullFileHasher>(_hasher)
            .BuildServiceProvider();
        _owned.Add(services);
        return new GameLocationsService(services);
    }

    /// <summary>
    /// Keeps the fingerprint rows in the fixture's connection through the same row code the real service runs. NSubstitute
    /// cannot stand in for it: matching a <see cref="Size"/> argument makes it construct one, which throws.
    /// </summary>
    private sealed class LocalRowsFileHashes(IConnection local, string gameKey, IDb[] stores, Task waitForBuild) : IFileHashesService
    {
        public IDb Current => local.Db;
        public IDb Local => local.Db;
        public IReadOnlyList<IDb> Databases => stores;
        public Task WaitForLocalBaselineBuild(GameInstallation installation, CancellationToken cancellationToken = default) => waitForBuild;

        public async Task RecordIndexedFiles(GameInstallation installation, IReadOnlyList<IndexedFileHash> files, CancellationToken cancellationToken = default)
        {
            using var tx = local.BeginTransaction();
            LocalFileHashRows.Record(tx, local.Db, gameKey, files);
            await tx.Commit();
        }

        public bool TryGetFingerprintedHash(GameInstallation installation, GamePath path, Size size, DateTimeOffset modifiedUtc, out Hash xxHash3)
            => LocalFileHashRows.TryGet(local.Db, gameKey, path, size, modifiedUtc, out xxHash3);

        public ValueTask<IDb> GetFileHashesDb() => throw new NotSupportedException();
        public Task CheckForUpdate(bool forceUpdate = false) => throw new NotSupportedException();
        public IEnumerable<VanityVersion> GetKnownVanityVersions(NexusModsGameId nexusModsGameId) => throw new NotSupportedException();
        public IEnumerable<GameFileRecord> GetGameFiles(LocatorIdsWithGameStore locatorIdsWithGameStore) => throw new NotSupportedException();
        public bool CoversInstalledBuild(LocatorIdsWithGameStore locatorIdsWithGameStore) => throw new NotSupportedException();
        public bool TryGetVanityVersion(LocatorIdsWithGameStore locatorIdsWithGameStore, out VanityVersion version) => throw new NotSupportedException();
        public bool TryGetLocatorIdsForVanityVersion(GameStore gameStore, VanityVersion version, out LocatorId[] locatorIds) => throw new NotSupportedException();
        public LocatorId[] GetLocatorIdsForVersionDefinition(GameStore gameStore, VersionDefinition.ReadOnly versionDefinition) => throw new NotSupportedException();
        public Task<LocalBaselineResult> EnsureLocalBaseline(GameInstallation installation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveLocalBaseline(GameStore store, string gamePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public DynamicData.Kernel.Optional<VersionData> SuggestVersionData(GameInstallation gameInstallation, IEnumerable<(GamePath Path, Hash Hash)> files) => throw new NotSupportedException();
    }

    private sealed class CountingHasher : IFullFileHasher
    {
        private readonly ConcurrentBag<AbsolutePath> _paths = [];
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;
        private int _peak;
        public int Calls => _paths.Count;
        public IReadOnlyCollection<AbsolutePath> Paths => _paths;
        public int PeakConcurrency => Volatile.Read(ref _peak);
        public Action<AbsolutePath>? OnHash { get; set; }
        public void Reset() => _paths.Clear();

        public async ValueTask<Hash> Hash(AbsolutePath file, CancellationToken cancellationToken)
        {
            _paths.Add(file);
            OnHash?.Invoke(file);
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while ((seen = Volatile.Read(ref _peak)) < now && Interlocked.CompareExchange(ref _peak, now, seen) != seen) { }

            // Parks every call until the cap's worth are in flight, or half a second has passed: with the cap
            // only that many calls can arrive, so waiting for more would deadlock the index.
            if (now >= HashingLimits.FileReaders) _gate.TrySetResult();
            else await Task.WhenAny(_gate.Task, Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken));
            try
            {
                return await file.XxHash3Async(token: cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentBag<IReadOnlyList<KeyValuePair<string, object?>>> _states = [];

        /// <summary>The named argument of the index summary line.</summary>
        public object? Value(string name) => _states
            .Select(s => s.FirstOrDefault(kv => kv.Key == name))
            .Single(kv => kv.Key == name).Value;

        public ILogger CreateLogger(string categoryName) => new Capture(_states);
        public void Dispose() { }

        private sealed class Capture(ConcurrentBag<IReadOnlyList<KeyValuePair<string, object?>>> states) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (state is IReadOnlyList<KeyValuePair<string, object?>> values && values.Any(kv => kv.Key == "FromFingerprint")) states.Add(values);
            }
        }
    }
}
