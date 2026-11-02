using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.Wabbajack.Tests.Acquire;

public class NestedArchiveCacheTests
{
    private sealed class FakeArchive : INestedArchive
    {
        public bool IsDisposed { get; private set; }

        public Task<Stream?> Open(RelativePath member, CancellationToken cancellationToken) => Task.FromResult<Stream?>(null);

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Hands out a new <see cref="FakeArchive"/> per open and counts them.</summary>
    private sealed class Opens
    {
        private int _count;
        public List<FakeArchive> Archives { get; } = [];
        public int Count => Volatile.Read(ref _count);

        public Task<INestedArchive> Open()
        {
            Interlocked.Increment(ref _count);
            var archive = new FakeArchive();
            lock (Archives) Archives.Add(archive);
            return Task.FromResult<INestedArchive>(archive);
        }
    }

    [Fact]
    public async Task AnArchiveAskedForAgainIsTheOneAlreadyOpen()
    {
        var opens = new Opens();
        await using var cache = new NestedArchiveCache(4);

        await using var first = await cache.Acquire("a", opens.Open);
        await using var second = await cache.Acquire("a", opens.Open);

        second.Archive.Should().BeSameAs(first.Archive);
        opens.Count.Should().Be(1);
    }

    [Fact]
    public async Task CallersAskingForOneKeyTogetherWaitForTheOneOpen()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        var archive = new FakeArchive();
        await using var cache = new NestedArchiveCache(4);

        async Task<INestedArchive> SlowOpen()
        {
            Interlocked.Increment(ref opens);
            await release.Task;
            return archive;
        }

        var asking = Enumerable.Range(0, 8).Select(_ => cache.Acquire("a", SlowOpen)).ToArray();
        release.SetResult();
        var leases = await Task.WhenAll(asking);

        opens.Should().Be(1);
        leases.Should().OnlyContain(lease => ReferenceEquals(lease.Archive, archive));
        foreach (var lease in leases) await lease.DisposeAsync();
    }

    [Fact]
    public async Task BeyondItsCapacityTheArchiveUsedLongestAgoIsDisposedWhenNothingIsReadingIt()
    {
        var opens = new Opens();
        await using var cache = new NestedArchiveCache(2);
        foreach (var key in new[] { "a", "b", "c" })
            await (await cache.Acquire(key, opens.Open)).DisposeAsync();

        opens.Archives.Select(a => a.IsDisposed).Should().Equal(true, false, false);
    }

    [Fact]
    public async Task AnArchiveBeingReadIsNotDisposedEvenWhenTheCacheIsOverCapacity()
    {
        var opens = new Opens();
        await using var cache = new NestedArchiveCache(1);
        var held = await cache.Acquire("a", opens.Open);
        var other = await cache.Acquire("b", opens.Open);
        opens.Archives.Should().OnlyContain(a => !a.IsDisposed, "both are being read, so neither can go");

        await other.DisposeAsync();

        opens.Archives[0].IsDisposed.Should().BeFalse("it is still being read");
        opens.Archives[1].IsDisposed.Should().BeTrue("back to the limit, the one nothing reads goes");
        await held.DisposeAsync();
        opens.Archives[0].IsDisposed.Should().BeFalse("one archive is within the limit");
    }

    [Fact]
    public async Task AFailedOpenFailsTheCallerAndIsNotRemembered()
    {
        var opens = new Opens();
        await using var cache = new NestedArchiveCache(4);

        var failure = await Record.ExceptionAsync(() => cache.Acquire("a", () => Task.FromException<INestedArchive>(new IOException("cannot read the container"))));
        failure.Should().BeOfType<IOException>();

        await using var again = await cache.Acquire("a", opens.Open);
        opens.Count.Should().Be(1);
    }

    [Fact]
    public async Task AFailedOpenFailsEveryCallerWaitingForItAndTheNextAskOpensAgain()
    {
        var release = new TaskCompletionSource<INestedArchive>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = new Opens();
        await using var cache = new NestedArchiveCache(4);

        var waiting = Enumerable.Range(0, 3).Select(_ => cache.Acquire("a", () => release.Task)).ToArray();
        release.SetException(new IOException("cannot read the container"));

        foreach (var task in waiting)
            (await Record.ExceptionAsync(() => task)).Should().BeOfType<IOException>();
        await using var again = await cache.Acquire("a", opens.Open);
        opens.Count.Should().Be(1);
    }

    [Fact]
    public async Task DisposingTheCacheDisposesTheIdleArchivesAtOnceAndTheBusyOnesWhenTheirLastReaderLetsGo()
    {
        var opens = new Opens();
        var cache = new NestedArchiveCache(4);
        await (await cache.Acquire("idle", opens.Open)).DisposeAsync();
        var busy = await cache.Acquire("busy", opens.Open);

        await cache.DisposeAsync();

        opens.Archives[0].IsDisposed.Should().BeTrue();
        opens.Archives[1].IsDisposed.Should().BeFalse("a copy is still reading it");

        await busy.DisposeAsync();

        opens.Archives[1].IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task ALeaseLetGoTwiceReleasesItsHoldOnce()
    {
        var opens = new Opens();
        await using var cache = new NestedArchiveCache(1);
        var first = await cache.Acquire("a", opens.Open);
        var second = await cache.Acquire("a", opens.Open);

        await first.DisposeAsync();
        await first.DisposeAsync();
        await cache.Acquire("b", opens.Open);

        opens.Archives[0].IsDisposed.Should().BeFalse("the second lease still reads it");
        await second.DisposeAsync();
    }

    [Fact]
    public async Task AskingAfterTheCacheIsDisposedIsRefused()
    {
        var cache = new NestedArchiveCache(4);
        await cache.DisposeAsync();

        var failure = await Record.ExceptionAsync(() => cache.Acquire("a", new Opens().Open));

        failure.Should().BeOfType<ObjectDisposedException>();
    }
}
