using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nerdbank.Streams;
using SilverModManager.ProxyConsole.Messages;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.ProxyConsole.Tests;

public class SerializerTests(IServiceProvider provider)
{
    private const int TimeoutMs = 60_000;

    [Fact(Timeout = TimeoutMs)]
    public async Task ReceivingWithNothingSentYetDoesNotBlockTheCaller()
    {
        var (near, far) = FullDuplexStream.CreatePair();
        var receiver = new Serializer(near, []);

        // The caller's own thread is the one a blocking read would hold, so the call is made on a thread of its own
        // and the test only asks whether the call came back.
        var call = Task.Factory.StartNew(() => receiver.ReceiveAsync(), TaskCreationOptions.LongRunning);
        var returned = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        returned.Should().BeSameAs(call, "ReceiveAsync has to hand back a pending task rather than wait for the peer on the caller's thread");

        var pending = await call;
        pending.IsCompleted.Should().BeFalse();

        await new Serializer(far, []).SendAsync(new ProgramArgumentsRequest());
        (await pending.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Should().BeOfType<ProgramArgumentsRequest>();
    }

    /// <summary>
    /// The renderable union is registered once for the whole process, by whichever serializer is built first. One
    /// built with no definitions (for messages that carry no renderable) must not use up that registration.
    /// Alone in a fresh test host this fails without the guard; in a shared one it passes whenever another test
    /// registered first.
    /// </summary>
    [Fact(Timeout = TimeoutMs)]
    public async Task ASerializerWithNoDefinitionsDoesNotKeepALaterOneFromRegisteringTheRenderables()
    {
        var (near, far) = FullDuplexStream.CreatePair();
        _ = new Serializer(near, []);

        await new Serializer(near, provider.GetRequiredService<IEnumerable<IRenderableDefinition>>()).SendAsync(new Render { Renderable = new Text { Template = "hello" } });
        var received = await new Serializer(far, []).ReceiveAsync();

        received.Should().BeOfType<Render>().Which.Renderable.Should().BeOfType<Text>().Which.Template.Should().Be("hello");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AMessageSentIsTheMessageReceived()
    {
        var (near, far) = FullDuplexStream.CreatePair();

        await new Serializer(near, []).SendAsync(new ProgramArgumentsResponse { Arguments = ["run", "a loadout"] });
        var received = await new Serializer(far, []).ReceiveAsync();

        received.Should().BeOfType<ProgramArgumentsResponse>().Which.Arguments.Should().Equal("run", "a loadout");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ASendWritesTheLengthAndTheBodyInOneWrite()
    {
        var writes = new WriteCountingStream();

        await new Serializer(writes, []).SendAsync(new ProgramArgumentsRequest());

        writes.Writes.Should().Be(1);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task APeerThatClosesBeforeSendingAMessageEndsTheReceiveWithEndOfStream()
    {
        var (near, far) = FullDuplexStream.CreatePair();
        var receiver = new Serializer(near, []);

        await far.DisposeAsync();

        await receiver.Invoking(r => r.ReceiveAsync()).Should().ThrowAsync<EndOfStreamException>();
    }

    private sealed class WriteCountingStream : Stream
    {
        public int Writes { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) => Writes++;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
