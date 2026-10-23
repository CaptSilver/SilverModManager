using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Backend.Games;
using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.MnemonicDB;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Storage;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NSubstitute;
using Connection = NexusMods.MnemonicDB.Connection;

namespace NexusMods.Backend.Tests.Games;

public sealed class GameLocationsServiceHashingTests : IDisposable
{
    private static readonly byte[] Content = "vanilla file contents"u8.ToArray();
    private static readonly Hash Sentinel = Hash.From(0xDEADBEEF);
    private static readonly Hash OtherSentinel = Hash.From(0xCAFEF00D);
    private static readonly RelativePath RelativeFile = RelativePath.FromUnsanitizedInput("data/file.bin");

    private readonly ServiceProvider _provider;
    private readonly List<IDisposable> _owned = [];
    private readonly TemporaryFileManager _temp = new(FileSystem.Shared);

    public GameLocationsServiceHashingTests()
    {
        _provider = new ServiceCollection()
            .AddLogging()
            .AddMnemonicDB()
            .AddHashRelationModel()
            .AddPathHashRelationModel()
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _temp.Dispose();
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _provider.Dispose();
    }

    [Fact]
    public async Task HashFile_UsesARowFoundOnlyInTheSecondStore()
    {
        var (file, realHash, minimalHash) = await WriteFixture();
        var empty = OpenStore();
        var populated = await OpenStoreWithRow(minimalHash, Sentinel);

        var result = await CreateService(empty, populated).HashFile(file, new GamePath(LocationId.Game, RelativeFile), CancellationToken.None);

        result.Should().Be(Sentinel);
        result.Should().NotBe(realHash);
    }

    [Fact]
    public async Task HashFile_StoresDisagreeingOnTheHash_FallsBackToHashing()
    {
        var (file, realHash, minimalHash) = await WriteFixture();
        var first = await OpenStoreWithRow(minimalHash, Sentinel);
        var second = await OpenStoreWithRow(minimalHash, OtherSentinel);

        var result = await CreateService(first, second).HashFile(file, new GamePath(LocationId.Game, RelativeFile), CancellationToken.None);

        result.Should().Be(realHash);
    }

    [Fact]
    public async Task HashFile_NoRowAnywhere_HashesTheFile()
    {
        var (file, realHash, _) = await WriteFixture();

        var result = await CreateService(OpenStore(), OpenStore()).HashFile(file, new GamePath(LocationId.Game, RelativeFile), CancellationToken.None);

        result.Should().Be(realHash);
    }

    [Fact]
    public async Task Lookup_NoRowForThePath_ReportsNoRowForPath()
    {
        var (file, _, minimalHash) = await WriteFixture();
        var populated = await OpenStoreWithRow(minimalHash, Sentinel);

        var lookup = await GameLocationsService.GetExistingHash([populated], file, new GamePath(LocationId.Game, RelativePath.FromUnsanitizedInput("data/other.bin")), CancellationToken.None);

        lookup.Hash.HasValue.Should().BeFalse();
        lookup.Miss.Should().Be(StoredHashMiss.NoRowForPath);
    }

    [Fact]
    public async Task Lookup_RowOfAnotherSize_ReportsSizeDiffers()
    {
        var (file, _, minimalHash) = await WriteFixture();
        var populated = await OpenStoreWithRow(minimalHash, Sentinel);
        await file.WriteAllBytesAsync([..Content, 1]);

        var lookup = await GameLocationsService.GetExistingHash([populated], file, new GamePath(LocationId.Game, RelativeFile), CancellationToken.None);

        lookup.Hash.HasValue.Should().BeFalse();
        lookup.Miss.Should().Be(StoredHashMiss.SizeDiffers);
    }

    [Fact]
    public async Task Lookup_RowOfTheSameSizeButOtherContent_ReportsContentDiffers()
    {
        var (file, _, minimalHash) = await WriteFixture();
        var populated = await OpenStoreWithRow(minimalHash, Sentinel);
        await file.WriteAllBytesAsync(Content.Select(b => (byte)(b ^ 0xFF)).ToArray());

        var lookup = await GameLocationsService.GetExistingHash([populated], file, new GamePath(LocationId.Game, RelativeFile), CancellationToken.None);

        lookup.Hash.HasValue.Should().BeFalse();
        lookup.Miss.Should().Be(StoredHashMiss.ContentDiffers);
    }

    [Fact]
    public async Task Lookup_StoresDisagreeing_ReportsStoresDisagree()
    {
        var (file, _, minimalHash) = await WriteFixture();
        var first = await OpenStoreWithRow(minimalHash, Sentinel);
        var second = await OpenStoreWithRow(minimalHash, OtherSentinel);

        var lookup = await GameLocationsService.GetExistingHash([first, second], file, new GamePath(LocationId.Game, RelativeFile), CancellationToken.None);

        lookup.Hash.HasValue.Should().BeFalse();
        lookup.Miss.Should().Be(StoredHashMiss.StoresDisagree);
    }

    [Fact]
    public async Task HashFile_RowSpelledWithDifferentCase_StillMatches()
    {
        var (file, realHash, minimalHash) = await WriteFixture();
        var populated = await OpenStoreWithRow(minimalHash, Sentinel);

        var result = await CreateService(populated).HashFile(file, new GamePath(LocationId.Game, RelativePath.FromUnsanitizedInput("DATA/File.BIN")), CancellationToken.None);

        result.Should().Be(Sentinel);
        result.Should().NotBe(realHash);
    }

    private async Task<(AbsolutePath File, Hash XxHash3, Hash MinimalHash)> WriteFixture()
    {
        var file = _temp.CreateFile().Path;
        await file.WriteAllBytesAsync(Content);
        var hashes = await MultiHasher.HashStream(new MemoryStream(Content));
        return (file, hashes.XxHash3, hashes.MinimalHash);
    }

    private GameLocationsService CreateService(params IDb[] stores)
    {
        var hashes = Substitute.For<IFileHashesService>();
        hashes.Databases.Returns(stores);
        // Current stays empty so only a lookup over Databases can find the rows.
        hashes.Current.Returns(OpenStore());

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(hashes)
            .BuildServiceProvider();
        _owned.Add(services);
        return new GameLocationsService(services);
    }

    private IDb OpenStore() => OpenConnection().Db;

    private IConnection OpenConnection()
    {
        var backend = new MnemonicDB.Storage.RocksDbBackend.Backend();
        var store = new DatomStore(_provider.GetRequiredService<ILogger<DatomStore>>(), DatomStoreSettings.InMemory, backend);
        var connection = new Connection(_provider.GetRequiredService<ILogger<Connection>>(), store, _provider, []);
        _owned.Add(backend);
        _owned.Add(store);
        _owned.Add(connection);
        return connection;
    }

    private async Task<IDb> OpenStoreWithRow(Hash minimalHash, Hash xxHash3)
    {
        var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        var hash = new HashRelation.New(tx)
        {
            MinimalHash = minimalHash,
            XxHash3 = xxHash3,
            Sha1 = default,
            XxHash64 = default,
            Md5 = default,
            Size = Size.From((ulong)Content.Length),
            Crc32 = default,
        };
        _ = new PathHashRelation.New(tx) { Path = RelativeFile, HashId = hash };
        var result = await tx.Commit();
        return result.Db;
    }
}
