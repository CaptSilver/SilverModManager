using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
using SilverModManager.Sdk.Hashes;
using NSubstitute;
using Connection = SilverModManager.MnemonicDB.Connection;

namespace SilverModManager.Backend.Tests.Games;

public sealed class GameLocationsServiceIndexTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly List<IDisposable> _owned = [];
    private readonly TemporaryFileManager _temp = new(FileSystem.Shared);
    private readonly CountingHasher _hasher = new();
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
        var hashes = Substitute.For<IFileHashesService>();
        hashes.Databases.Returns(stores ?? []);
        hashes.WaitForLocalBaselineBuild(Arg.Any<GameInstallation>(), Arg.Any<CancellationToken>()).Returns(waitForBuild ?? Task.CompletedTask);

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(hashes)
            .AddSingleton<IFullFileHasher>(_hasher)
            .BuildServiceProvider();
        _owned.Add(services);
        return new GameLocationsService(services);
    }

    private sealed class CountingHasher : IFullFileHasher
    {
        private readonly ConcurrentBag<AbsolutePath> _paths = [];
        public int Calls => _paths.Count;
        public IReadOnlyCollection<AbsolutePath> Paths => _paths;
        public void Reset() => _paths.Clear();

        public async ValueTask<Hash> Hash(AbsolutePath file, CancellationToken cancellationToken)
        {
            _paths.Add(file);
            return await file.XxHash3Async(token: cancellationToken);
        }
    }
}
