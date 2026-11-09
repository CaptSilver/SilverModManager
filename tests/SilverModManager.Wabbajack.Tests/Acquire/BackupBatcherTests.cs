using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.Wabbajack.Tests.Acquire;

public class BackupBatcherTests
{
    private static readonly TimeSpan Never = TimeSpan.FromMinutes(10);

    private static ArchivedFileEntry Entry(int seed, int length = 16)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return new ArchivedFileEntry(new MemoryStreamFactory($"file{seed}", new MemoryStream(bytes)), FileIdentity.XxHash3(bytes.xxHash3()), Size.FromLong(length));
    }

    /// <summary>Takes every call to BackupFiles, remembers the batch, and fails it if <paramref name="refuses"/> says so.</summary>
    private sealed class RecordingStore(Func<IReadOnlyList<ArchivedFileEntry>, Exception?>? refuses = null) : ForwardingFileStore(Substitute.For<IFileStore>())
    {
        private readonly List<ArchivedFileEntry[]> _batches = [];

        public List<ArchivedFileEntry[]> Batches { get { lock (_batches) return [.. _batches]; } }

        public override Task BackupFiles(IEnumerable<ArchivedFileEntry> backups, bool deduplicate = true, CancellationToken token = default, IProgress<double>? progress = null)
        {
            var batch = backups.ToArray();
            lock (_batches) _batches.Add(batch);
            var failure = refuses?.Invoke(batch);
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }
    }

    private static async Task Completes(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task FilesGoToTheStoreTogetherOnceTheBatchHoldsTheLimitOfThem()
    {
        var store = new RecordingStore();
        var batcher = new BackupBatcher(store, CancellationToken.None, maxEntries: 3, maxWait: Never);

        var added = new[] { batcher.Add(Entry(1)), batcher.Add(Entry(2)), batcher.Add(Entry(3)) };

        foreach (var task in added) await Completes(task);
        store.Batches.Should().ContainSingle().Which.Should().HaveCount(3);
    }

    [Fact]
    public async Task FilesGoToTheStoreOnceTheyAddUpToTheByteLimit()
    {
        var store = new RecordingStore();
        var batcher = new BackupBatcher(store, CancellationToken.None, maxEntries: 100, maxBytes: 40, maxWait: Never);

        await Completes(Task.WhenAll(batcher.Add(Entry(1, 16)), batcher.Add(Entry(2, 16)), batcher.Add(Entry(3, 16))));

        store.Batches.Should().ContainSingle().Which.Should().HaveCount(3);
    }

    [Fact]
    public async Task AFileWaitsNoLongerThanTheMaximumWaitForCompanyThatDoesNotCome()
    {
        var store = new RecordingStore();
        var batcher = new BackupBatcher(store, CancellationToken.None, maxEntries: 100, maxWait: TimeSpan.FromMilliseconds(20));

        await Completes(batcher.Add(Entry(1)));

        store.Batches.Should().ContainSingle().Which.Should().HaveCount(1);
    }

    [Fact]
    public async Task AFileThatArrivesAfterAFlushStartsTheNextBatch()
    {
        var store = new RecordingStore();
        var batcher = new BackupBatcher(store, CancellationToken.None, maxEntries: 2, maxWait: TimeSpan.FromMilliseconds(20));

        await Completes(Task.WhenAll(batcher.Add(Entry(1)), batcher.Add(Entry(2))));
        await Completes(batcher.Add(Entry(3)));

        store.Batches.Select(b => b.Length).Should().Equal(2, 1);
    }

    [Fact]
    public async Task OneFileTheStoreRefusesFailsAloneAndTheOthersInItsBatchAreStored()
    {
        var bad = Entry(2);
        var store = new RecordingStore(batch => batch.Contains(bad) ? new InvalidDataException("cannot read that one") : null);
        var batcher = new BackupBatcher(store, CancellationToken.None, maxEntries: 4, maxWait: Never);

        var good = new[] { batcher.Add(Entry(1)), batcher.Add(Entry(3)), batcher.Add(Entry(4)) };
        var refused = batcher.Add(bad);

        foreach (var task in good) await Completes(task);
        var failure = await Record.ExceptionAsync(() => Completes(refused));
        failure.Should().BeOfType<InvalidDataException>();
    }

    [Fact]
    public async Task ABatchTheStoreRefusesWholeFailsEveryFileInIt()
    {
        var store = new RecordingStore(_ => new IOException("The disk is full"));
        var batcher = new BackupBatcher(store, CancellationToken.None, maxEntries: 3, maxWait: Never);

        var added = new[] { batcher.Add(Entry(1)), batcher.Add(Entry(2)), batcher.Add(Entry(3)) };

        foreach (var task in added)
            (await Record.ExceptionAsync(() => Completes(task))).Should().BeOfType<IOException>();
    }

    [Fact]
    public async Task ACancelledBatchIsNotRetriedFileByFile()
    {
        using var cancel = new CancellationTokenSource();
        var store = new RecordingStore(_ => new OperationCanceledException());
        var batcher = new BackupBatcher(store, cancel.Token, maxEntries: 3, maxWait: Never);
        await cancel.CancelAsync();

        var added = new[] { batcher.Add(Entry(1)), batcher.Add(Entry(2)), batcher.Add(Entry(3)) };

        foreach (var task in added)
            (await Record.ExceptionAsync(() => Completes(task))).Should().BeAssignableTo<OperationCanceledException>();
        store.Batches.Should().ContainSingle();
    }

    private sealed class BlockingStore : ForwardingFileStore
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public BlockingStore() : base(Substitute.For<IFileStore>()) { }

        public int Started => Volatile.Read(ref _started);
        public void Release() => _release.TrySetResult();

        public override async Task BackupFiles(IEnumerable<ArchivedFileEntry> backups, bool deduplicate = true, CancellationToken token = default, IProgress<double>? progress = null)
        {
            Interlocked.Increment(ref _started);
            await _release.Task;
        }
    }

    [Fact]
    public async Task BatchesBeingStoredAreCountedUntilTheyFinish()
    {
        var store = new BlockingStore();
        var batcher = new BackupBatcher(store, CancellationToken.None, maxEntries: 2, maxWait: Never);

        var added = new[] { batcher.Add(Entry(1)), batcher.Add(Entry(2)), batcher.Add(Entry(3)), batcher.Add(Entry(4)) };
        while (store.Started < 2) await Task.Delay(5);

        batcher.InFlight.Should().Be(2);

        store.Release();
        foreach (var task in added) await Completes(task);
        batcher.InFlight.Should().Be(0);
    }
}
