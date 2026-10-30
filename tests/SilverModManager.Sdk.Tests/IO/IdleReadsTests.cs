using FluentAssertions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Sdk.Tests.IO;

public class IdleReadsTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    /// <summary>Hands out <paramref name="data"/> and then waits for the cancellation it is given, as a dead socket does.</summary>
    private sealed class StallingStream(byte[] data) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < data.Length)
            {
                var count = Math.Min(buffer.Length, data.Length - _position);
                data.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    [Fact]
    public async Task ABodyThatIsReadInTime_ReturnsItsBytes()
    {
        await using var source = new MemoryStream([1, 2, 3, 4]);
        var buffer = new byte[16];

        var read = await IdleReads.ReadAsync(source, buffer, "files.test", Short, TimeProvider.System, CancellationToken.None);

        read.Should().Be(4);
        buffer.Take(4).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task ASilentStream_FailsWithAMessageNamingTheHost()
    {
        await using var source = new StallingStream([]);

        var thrown = await FluentActions.Awaiting(async () =>
            await IdleReads.ReadAsync(source, new byte[16], "files.test", Short, TimeProvider.System, CancellationToken.None)
        ).Should().ThrowAsync<DownloadStalledException>();

        thrown.Which.Message.Should().Be("files.test stopped sending data");
        thrown.Which.Host.Should().Be("files.test");
    }

    [Fact]
    public async Task TheUserCancelling_IsACancellationNotAStall()
    {
        await using var source = new StallingStream([]);
        using var cts = new CancellationTokenSource();
        var read = IdleReads.ReadAsync(source, new byte[16], "files.test", TimeSpan.FromMinutes(5), TimeProvider.System, cts.Token).AsTask();

        await cts.CancelAsync();

        await FluentActions.Awaiting(() => read).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReadToEnd_ReturnsEveryByteOfASlowBody()
    {
        var data = new byte[300_000];
        Random.Shared.NextBytes(data);
        await using var source = new MemoryStream(data);

        var bytes = await IdleReads.ReadToEndAsync(source, "files.test", Short, TimeProvider.System, CancellationToken.None);

        bytes.Should().Equal(data);
    }

    [Fact]
    public async Task ReadToEnd_FailsWhenTheBodyStopsHalfWay()
    {
        await using var source = new StallingStream(new byte[1000]);

        await FluentActions.Awaiting(async () =>
            await IdleReads.ReadToEndAsync(source, "files.test", Short, TimeProvider.System, CancellationToken.None)
        ).Should().ThrowAsync<DownloadStalledException>();
    }
}
