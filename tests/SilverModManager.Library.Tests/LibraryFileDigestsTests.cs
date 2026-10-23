using System.Security.Cryptography;
using FluentAssertions;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Backend;
using SilverModManager.Library.Tests.DownloadsService;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Threading;

namespace SilverModManager.Library.Tests;

public class LibraryFileDigestsTests(IConnection connection, IServiceProvider serviceProvider, TemporaryFileManager temporaryFileManager)
{
    private static readonly byte[] Content = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7 + 1)).ToArray();

    private async Task<LibraryFile.ReadOnly> AddOldRow(byte[] content)
    {
        using var tx = connection.BeginTransaction();
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = "old.bin",
            Hash = content.xxHash3(),
            Size = Size.From((ulong)content.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = "old.bin" },
        };
        var result = await tx.Commit();
        return result.Remap(file);
    }

    [Fact]
    public async Task OldRowGetsItsSha256OnFirstCallAndIsNotReReadOnTheSecond()
    {
        var old = await AddOldRow(Content);
        old.Sha256.HasValue.Should().BeFalse("the row predates the attribute");

        var store = new CountingFileStore(old.Hash, Content);
        var digests = new LibraryFileDigests(store, connection);
        var expected = Sha256Value.From(SHA256.HashData(Content));

        var first = await digests.EnsureSha256(old, TestContext.Current.CancellationToken);
        first.Should().Be(expected);
        store.Reads.Should().Be(1);

        var reloaded = LibraryFile.Load(connection.Db, old.Id);
        reloaded.Sha256.Value.Should().Be(expected, "the digest is written back");

        var second = await digests.EnsureSha256(reloaded, TestContext.Current.CancellationToken);
        second.Should().Be(expected);
        store.Reads.Should().Be(1, "the stored digest answers the second call");
    }

    [Fact]
    public async Task ASnapshotFromBeforeTheWriteBackDoesNotReReadTheStore()
    {
        var old = await AddOldRow(Content);
        var store = new CountingFileStore(old.Hash, Content);
        var digests = new LibraryFileDigests(store, connection);

        await digests.EnsureSha256(old, TestContext.Current.CancellationToken);
        var second = await digests.EnsureSha256(old, TestContext.Current.CancellationToken);

        second.Should().Be(Sha256Value.From(SHA256.HashData(Content)));
        store.Reads.Should().Be(1, "the digest written by the first call is in the current database");
    }

    [Fact]
    public async Task FileMissingFromTheStoreYieldsNullAndWritesNothing()
    {
        var old = await AddOldRow(Content);
        var store = new CountingFileStore(default, []);
        var digests = new LibraryFileDigests(store, connection);

        var result = await digests.EnsureSha256(old, TestContext.Current.CancellationToken);

        result.Should().BeNull();
        LibraryFile.Load(connection.Db, old.Id).Sha256.HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task EnsureXxHash64FillsANonArchiveRowFromTheStore()
    {
        var old = await AddOldRow(Content);
        old.XxHash64.HasValue.Should().BeFalse("the row predates the attribute");
        var store = new CountingFileStore(old.Hash, Content);
        var digests = new LibraryFileDigests(store, connection);

        var first = await digests.EnsureXxHash64(old, TestContext.Current.CancellationToken);

        first.Should().Be(Xx64Hasher.Hash(Content));
        store.Reads.Should().Be(1);
        LibraryFile.Load(connection.Db, old.Id).XxHash64.Value.Should().Be(Xx64Hasher.Hash(Content), "the value is written back");

        var second = await digests.EnsureXxHash64(LibraryFile.Load(connection.Db, old.Id), TestContext.Current.CancellationToken);
        second.Should().Be(first);
        store.Reads.Should().Be(1, "the recorded value answers the second call");
    }

    [Fact]
    public async Task EnsureXxHash64IsNullForANonArchiveRowWhoseBytesAreNotInTheStore()
    {
        var old = await AddOldRow(Content);
        var digests = new LibraryFileDigests(new CountingFileStore(default, []), connection);

        var result = await digests.EnsureXxHash64(old, TestContext.Current.CancellationToken);

        result.Should().BeNull();
        LibraryFile.Load(connection.Db, old.Id).XxHash64.HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task EnsureXxHash64IsNullForAnArchiveRowWithoutOneAndDoesNotReadTheStore()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var zipPath = folder.Path.Combine("pack.zip");
        using (var zip = new System.IO.Compression.ZipArchive(File.Create(zipPath.ToString()), System.IO.Compression.ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry("a.txt").Open();
            entry.Write("hello"u8);
        }
        LibraryFile.ReadOnly added;
        using (var addTx = connection.BeginTransaction())
        {
            var newFile = await AddLibraryFileJob.Create(serviceProvider, addTx, zipPath);
            added = (await addTx.Commit()).Remap(newFile);
        }
        added.XxHash64.HasValue.Should().BeTrue();

        // A row added before the attribute existed has no value.
        using (var tx = connection.BeginTransaction())
        {
            tx.Retract(added.Id, LibraryFile.XxHash64, added.XxHash64.Value);
            await tx.Commit();
        }
        var archiveRow = LibraryFile.Load(connection.Db, added.Id);
        archiveRow.XxHash64.HasValue.Should().BeFalse();

        var store = new CountingFileStore(archiveRow.Hash, Content);
        var digests = new LibraryFileDigests(store, connection);

        var result = await digests.EnsureXxHash64(archiveRow, TestContext.Current.CancellationToken);

        result.Should().BeNull();
        store.Reads.Should().Be(0);
        LibraryFile.Load(connection.Db, added.Id).XxHash64.HasValue.Should().BeFalse();
    }

    private sealed class CountingFileStore(Hash known, byte[] content) : IFileStore
    {
        public int Reads { get; private set; }

        public ValueTask<bool> HaveFile(Hash hash) => ValueTask.FromResult(content.Length > 0 && hash == known);

        public Task<Stream> GetFileStream(Hash hash, CancellationToken token = default)
        {
            Reads++;
            return Task.FromResult<Stream>(new MemoryStream(content, writable: false));
        }

        public Task BackupFiles(IEnumerable<ArchivedFileEntry> backups, bool deduplicate = true, CancellationToken token = default) => throw new NotSupportedException();
        public Task ExtractFiles(IEnumerable<(Hash Hash, AbsolutePath Dest)> files, CancellationToken token = default, Action<(int Current, int Max)>? progress = null) => throw new NotSupportedException();
        public Task<Dictionary<Hash, byte[]>> ExtractFiles(IEnumerable<Hash> files, CancellationToken token = default) => throw new NotSupportedException();
        public Task<byte[]> Load(Hash hash, CancellationToken token = default) => throw new NotSupportedException();
        public AsyncFriendlyReaderWriterLock.WriteLockDisposable WriteLock() => throw new NotSupportedException();
        public void ReloadCaches() => throw new NotSupportedException();
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services
                .AddLibraryModels()
                .AddOSInterop()
                .AddFileExtractors();
        }
    }
}
