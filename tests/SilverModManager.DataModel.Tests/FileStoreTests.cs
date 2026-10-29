using System.Text;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.GC;
using SilverModManager.Abstractions.Library;
using SilverModManager.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.DataModel.Tests;

public class FileStoreTests : ACyberpunkIsolatedGameTest<FileStoreTests>
{
    private readonly ILibraryService _libraryService;
    private readonly IFileStore _fileStore;
    private readonly IConnection _connection;
    private readonly IGarbageCollectorRunner _gcRunner;

    public FileStoreTests(ITestOutputHelper helper) : base(helper)
    {
        _libraryService = ServiceProvider.GetRequiredService<ILibraryService>();
        _fileStore = ServiceProvider.GetRequiredService<IFileStore>();
        _connection = ServiceProvider.GetRequiredService<IConnection>();
        _gcRunner = ServiceProvider.GetRequiredService<IGarbageCollectorRunner>();
    }
    
    
    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Reports { get; } = [];
        public void Report(double value) => Reports.Add(value);
    }

    private static ArchivedFileEntry RandomEntry(int seed, int length, out byte[] bytes)
    {
        bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return new ArchivedFileEntry(new MemoryStreamFactory($"random{seed}.bin", new MemoryStream(bytes)), bytes.xxHash3(), Size.FromLong(length));
    }

    [Fact]
    public async Task BackingUpFiles_ReportsProgressThatNeverGoesBackAndEndsAtOne()
    {
        var entries = Enumerable.Range(1, 12).Select(i => RandomEntry(i, 700 * 1024, out _)).ToList();
        var progress = new RecordingProgress();

        await _fileStore.BackupFiles(entries, deduplicate: false, token: TestContext.Current.CancellationToken, progress: progress);

        progress.Reports.Should().HaveCountGreaterThan(1, "the packer reports as its blocks finish, not only at the end");
        progress.Reports.Should().BeInAscendingOrder("a bar fed by it must not move backwards");
        progress.Reports.Should().OnlyContain(value => value >= 0 && value <= 1);
        progress.Reports.Last().Should().Be(1.0);
    }

    [Fact]
    public async Task BackingUpFiles_StoresBytesThatReadBackIdenticallyAtTheBackupCompressionLevel()
    {
        // One file past the chunk size and several below it, so both the chunked and the solid path are used.
        var big = RandomEntry(100, 6 * 1024 * 1024 + 123, out var bigBytes);
        var smallBytes = new Dictionary<Hash, byte[]>();
        var small = Enumerable.Range(1, 5).Select(i =>
        {
            var entry = RandomEntry(i, 40 * 1024 + i, out var bytes);
            smallBytes[entry.Hash] = bytes;
            return entry;
        }).ToList();
        var compressible = new ArchivedFileEntry(new MemoryStreamFactory("text.txt", new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("silver ", 100_000))))),
            Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("silver ", 100_000))).xxHash3(), Size.FromLong("silver ".Length * 100_000));

        await _fileStore.BackupFiles([big, compressible, .. small], deduplicate: false, token: TestContext.Current.CancellationToken);

        (await _fileStore.Load(big.Hash, TestContext.Current.CancellationToken)).Should().Equal(bigBytes);
        (await _fileStore.Load(compressible.Hash, TestContext.Current.CancellationToken)).Should().Equal(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("silver ", 100_000))));
        foreach (var entry in small) (await _fileStore.Load(entry.Hash, TestContext.Current.CancellationToken)).Should().Equal(smallBytes[entry.Hash]);
    }

    [Fact]
    public async Task BackingUpNothingNewReportsCompletion_WhenThereIsNothingToPack()
    {
        var progress = new RecordingProgress();
        await _fileStore.BackupFiles([], deduplicate: false, token: TestContext.Current.CancellationToken, progress: progress);
        progress.Reports.Should().Equal(1.0);
    }

    [Fact]
    public async Task BackingUpFilesAlreadyStoredReportsCompletion_WhenEverythingIsDeduplicated()
    {
        var entry = RandomEntry(7, 50 * 1024, out _);
        await _fileStore.BackupFiles([entry], deduplicate: false, token: TestContext.Current.CancellationToken);
        var again = RandomEntry(7, 50 * 1024, out _);
        var progress = new RecordingProgress();

        await _fileStore.BackupFiles([again], deduplicate: true, token: TestContext.Current.CancellationToken, progress: progress);

        progress.Reports.Should().Equal(1.0);
    }

    [Fact]
    public async Task AFileThatChangedAfterItWasHashedIsRejectedAndNotIndexed()
    {
        // The bytes a caller packs must be the bytes it hashed: a file rewritten in between would otherwise be
        // stored under a hash that names different content.
        var hashed = "bytes the caller hashed"u8.ToArray();
        var packed = "bytes that replaced them"u8.ToArray();
        var hash = hashed.xxHash3();
        var entry = new ArchivedFileEntry(new MemoryStreamFactory("changed.bin", new MemoryStream(packed)), hash, Size.FromLong(packed.Length));

        var act = async () => await _fileStore.BackupFiles([entry]);

        await act.Should().ThrowAsync<KeyNotFoundException>();
        (await _fileStore.HaveFile(hash)).Should().BeFalse();
        (await _fileStore.HaveFile(packed.xxHash3())).Should().BeFalse();
    }

    [Fact]
    [GithubIssue(2944)]
    public async Task DeletingModWithSharedFileDoesNotCorrupt()
    {
        // Arrange
        var pathA = FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources").Combine("Lookup Anything 1.48.1-541-1-48-1-1739333325.zip");
        var pathB = FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources").Combine("Lookup Anything 1.49.0-541-1-49-0-1740624009.zip");

        var libraryItemA = await _libraryService.AddLocalFile(pathA);
        var libraryItemB = await _libraryService.AddLocalFile(pathB);

        // Get the hashes of the files in the library for item B
        var hashesBBefore = LibraryArchiveFileEntry.FindByParent(_connection.Db, libraryItemB)
            .Select(x => x.AsLibraryFile());
        var dataFileBBefore = hashesBBefore.FirstOrOptional(x => x.FileName == "data.json");
        dataFileBBefore.HasValue.Should().BeTrue();
        
        var dataFileBHashBefore = dataFileBBefore.Value.Hash;
        
        // Act
        await _libraryService.RemoveLibraryItems([libraryItemA.AsLibraryFile().AsLibraryItem()], GarbageCollectorRunMode.RunSynchronously);
        
        // Assert
        // Changing A should not affect B

        var hashesBAfter = LibraryArchiveFileEntry.FindByParent(_connection.Db, libraryItemB)
            .Select(x => x.AsLibraryFile());
        
        var dataFileBAfter = hashesBAfter.FirstOrOptional(x => x.FileName == "data.json");
        dataFileBAfter.HasValue.Should().BeTrue();
        
        dataFileBAfter.Value.Hash.Should().Be(dataFileBHashBefore, "Hash should be the same after changing A");

        {
            await using var dataFileBStreamAfter = await _fileStore.GetFileStream(dataFileBAfter.Value.Hash);
            var dataFileBCheckedHashAfter = await dataFileBStreamAfter.xxHash3Async();
            dataFileBCheckedHashAfter.Should().Be(dataFileBHashBefore, "Hash should be the same after changing A");
        }
    }

    [Fact]
    public async Task Test_ArchiveValidation()
    {
        var bytes = "Hello World"u8.ToArray();
        var ms = new MemoryStream(buffer: bytes);
        var entry = new ArchivedFileEntry(new MemoryStreamFactory("", ms), Hash: Hash.Zero, Size.FromLong(ms.Length));

        var act = async () => await _fileStore.BackupFiles([entry], deduplicate: false);
        await act.Should().ThrowAsync<KeyNotFoundException>(because: "file requested to be backed up doesn't exist in the archive due to bad hash");
    }
}
