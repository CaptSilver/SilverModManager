using FluentAssertions;
using SilverModManager.Games.Bethesda.Archives;
using NexusMods.Paths;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.Wabbajack.Tests.Acquire;

public class BsaMemberOpenerTests : IDisposable
{
    private readonly TemporaryFileManager _temp = new(
        FileSystem.Shared,
        FileSystem.Shared.FromUnsanitizedFullPath(TestScratch.Combine($"Wabbajack.Tests-{Guid.NewGuid()}")));

    private readonly BsaMemberOpener _opener = new();

    public void Dispose() => _temp.Dispose();

    private static RelativePath P(string path) => RelativePath.FromUnsanitizedInput(path);

    private static async Task<byte[]> ReadAll(Stream? stream)
    {
        stream.Should().NotBeNull();
        await using (stream)
        {
            using var copy = new MemoryStream();
            await stream!.CopyToAsync(copy);
            return copy.ToArray();
        }
    }

    [Theory]
    [InlineData("pack.bsa", true)]
    [InlineData("Data/PACK.BA2", true)]
    [InlineData("pack.zip", false)]
    [InlineData("pack.bsa.txt", false)]
    public void OnlyBethesdaArchivesCanBeOpened(string name, bool expected)
        => _opener.CanOpen(P(name)).Should().Be(expected);

    [Fact]
    public async Task AMemberIsFoundByItsPathWhateverTheCase()
    {
        var archive = await TestArchives.Tes5Archive(_temp);

        var member = await _opener.Open(new MemoryStream(archive), P("Meshes\\FIRST.nif"), CancellationToken.None);

        (await ReadAll(member)).Should().Equal(TestArchives.FirstMemberData);
    }

    [Fact]
    public async Task AnOpenedArchiveGivesAMemberByItsPathWhateverTheCase()
    {
        var bytes = await TestArchives.Tes5Archive(_temp);
        await using var archive = await _opener.OpenArchive(_ => Task.FromResult<Stream>(new MemoryStream(bytes)), CancellationToken.None);

        (await ReadAll(await archive.Open(P("Meshes\\FIRST.nif"), CancellationToken.None))).Should().Equal(TestArchives.FirstMemberData);
        (await ReadAll(await archive.Open(TestArchives.SecondMemberPath, CancellationToken.None))).Should().Equal(TestArchives.SecondMemberData);
        (await archive.Open(P("meshes/absent.nif"), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AnOpenedArchiveReadsItsIndexOnceHoweverManyMembersAreAskedForOneAfterAnother()
    {
        var bytes = await TestArchives.Tes5Archive(_temp);
        var containers = 0;
        await using var archive = await _opener.OpenArchive(_ =>
        {
            Interlocked.Increment(ref containers);
            return Task.FromResult<Stream>(new MemoryStream(bytes));
        }, CancellationToken.None);

        for (var i = 0; i < 20; i++)
            await ReadAll(await archive.Open(TestArchives.FirstMemberPath, CancellationToken.None));

        containers.Should().Be(1);
    }

    [Fact]
    public async Task AnOpenedArchiveServesMembersToSeveralThreadsAtOnce()
    {
        var bytes = await TestArchives.Tes5Archive(_temp);
        await using var archive = await _opener.OpenArchive(_ => Task.FromResult<Stream>(new MemoryStream(bytes)), CancellationToken.None);

        var reads = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
            await ReadAll(await archive.Open(i % 2 == 0 ? TestArchives.FirstMemberPath : TestArchives.SecondMemberPath, CancellationToken.None)))));

        reads.Where((_, i) => i % 2 == 0).Should().OnlyContain(read => read.SequenceEqual(TestArchives.FirstMemberData));
        reads.Where((_, i) => i % 2 == 1).Should().OnlyContain(read => read.SequenceEqual(TestArchives.SecondMemberData));
    }

    [Fact]
    public async Task DisposingAnOpenedArchiveDisposesTheContainerStreamsItHolds()
    {
        var bytes = await TestArchives.Tes5Archive(_temp);
        var streams = new List<TrackedStream>();
        var archive = await _opener.OpenArchive(_ =>
        {
            var stream = new TrackedStream(bytes);
            lock (streams) streams.Add(stream);
            return Task.FromResult<Stream>(stream);
        }, CancellationToken.None);
        await ReadAll(await archive.Open(TestArchives.FirstMemberPath, CancellationToken.None));
        streams.Should().OnlyContain(stream => !stream.IsDisposed, "the next member reads from it");

        await archive.DisposeAsync();

        streams.Should().NotBeEmpty().And.OnlyContain(stream => stream.IsDisposed);
    }

    [Fact]
    public async Task AnOpenedArchiveHoldsNoMoreContainerStreamsThanItHasReaders()
    {
        var bytes = await TestArchives.Tes5Archive(_temp);
        var open = 0;
        var mostOpen = 0;
        await using var archive = await new BsaMemberOpener(readersPerArchive: 3).OpenArchive(_ =>
        {
            var now = Interlocked.Increment(ref open);
            int seen;
            while ((seen = Volatile.Read(ref mostOpen)) < now && Interlocked.CompareExchange(ref mostOpen, now, seen) != seen) { }
            return Task.FromResult<Stream>(new MemoryStream(bytes));
        }, CancellationToken.None);

        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(async () =>
            await ReadAll(await archive.Open(TestArchives.FirstMemberPath, CancellationToken.None)))));

        mostOpen.Should().BeInRange(1, 3);
    }

    [Fact]
    public async Task AContainerThatIsNotAnArchiveIsAMemberNotFoundWhenItIsOpened()
    {
        var failure = await Record.ExceptionAsync(async () =>
        {
            await using var archive = await _opener.OpenArchive(_ => Task.FromResult<Stream>(new MemoryStream(new byte[100])), CancellationToken.None);
            await archive.Open(P("meshes/a.nif"), CancellationToken.None);
        });

        failure.Should().BeOfType<MemberNotFoundException>().Which.Message.Should().Contain("meshes/a.nif");
    }

    [Fact]
    public async Task AContainerStreamThatFailsWhileTheIndexIsReadIsDisposedAndTheFailureSurfaces()
        => await AFailedIndexReadDisposesTheContainerStream(() => new IOException("disk went away"), typeof(IOException));

    [Fact]
    public async Task AContainerStreamIsDisposedWhenTheIndexReadIsCancelled()
        => await AFailedIndexReadDisposesTheContainerStream(() => new OperationCanceledException(), typeof(OperationCanceledException));

    private async Task AFailedIndexReadDisposesTheContainerStream(Func<Exception> failure, Type expected)
    {
        var bytes = await TestArchives.Tes5Archive(_temp);
        var stream = new FailingStream(bytes, failure);
        await using var archive = await _opener.OpenArchive(_ => Task.FromResult<Stream>(stream), CancellationToken.None);

        var thrown = await Record.ExceptionAsync(() => archive.Open(TestArchives.FirstMemberPath, CancellationToken.None));

        thrown.Should().BeOfType(expected);
        stream.IsDisposed.Should().BeTrue("nothing else holds it once the archive could not be opened");
    }

    private sealed class FailingStream(byte[] bytes, Func<Exception> failure) : MemoryStream(bytes)
    {
        public bool IsDisposed { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) => throw failure();
        public override int Read(Span<byte> buffer) => throw failure();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw failure();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw failure();

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class TrackedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task AMemberTheArchiveLacksIsNull()
    {
        var archive = await TestArchives.Tes5Archive(_temp);

        (await _opener.Open(new MemoryStream(archive), P("meshes/absent.nif"), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AnArchiveStreamThatCannotSeekIsStillRead()
    {
        var archive = await TestArchives.Tes5Archive(_temp);

        var member = await _opener.Open(new ForwardOnlyStream(new MemoryStream(archive)), TestArchives.SecondMemberPath, CancellationToken.None);

        (await ReadAll(member)).Should().Equal(TestArchives.SecondMemberData);
    }

    [Fact]
    public async Task BytesThatAreNotAnArchiveAreAMemberNotFoundNamingTheMember()
    {
        var failure = await Record.ExceptionAsync(() => _opener.Open(new MemoryStream(new byte[100]), P("meshes/a.nif"), CancellationToken.None));

        failure.Should().BeOfType<MemberNotFoundException>().Which.Message.Should().Contain("meshes/a.nif");
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
