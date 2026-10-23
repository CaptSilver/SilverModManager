using System.Security.Cryptography;
using NexusMods.Wabbajack.Transforms;
using Octodiff.Core;
using Octodiff.Diagnostics;

namespace NexusMods.Wabbajack.Transforms.Tests;

public class PatchTests
{
    private static byte[] Random(int length, int seed)
    {
        var bytes = new byte[length];
        new System.Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// A target that keeps most of the basis, so the delta is mostly copy commands, with a changed
    /// stretch and an appended tail so it also carries literal data.
    /// </summary>
    private static byte[] Edit(byte[] basis)
    {
        var target = new byte[basis.Length + 777];
        basis.CopyTo(target, 0);
        new System.Random(99).NextBytes(target.AsSpan(basis.Length / 3, Math.Min(5000, basis.Length / 3)));
        new System.Random(100).NextBytes(target.AsSpan(basis.Length));
        return target;
    }

    private static byte[] BuildDelta(byte[] basis, byte[] target)
    {
        var signature = new MemoryStream();
        new SignatureBuilder().Build(new MemoryStream(basis), new SignatureWriter(signature));
        signature.Position = 0;

        var delta = new MemoryStream();
        new DeltaBuilder().BuildDelta(
            new MemoryStream(target),
            new SignatureReader(signature, new NullProgressReporter()),
            new AggregateCopyOperationsDecorator(new BinaryDeltaWriter(delta)));
        return delta.ToArray();
    }

    /// <summary>Hides Seek and Length so the basis looks like a network or decompression stream.</summary>
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(20 * 1024 * 1024)]
    public async Task Apply_RebuildsTheTargetByteForByte(int basisLength)
    {
        var basis = Random(basisLength, 1);
        var target = Edit(basis);
        var delta = BuildDelta(basis, target);

        var output = new MemoryStream();
        await OctodiffPatch.Apply(new MemoryStream(basis), new MemoryStream(delta), output, CancellationToken.None);

        Assert.True(target.AsSpan().SequenceEqual(output.ToArray()));
    }

    [Fact]
    public async Task Apply_AcceptsANonSeekableBasis()
    {
        var basis = Random(64 * 1024, 2);
        var target = Edit(basis);
        var delta = BuildDelta(basis, target);

        var output = new MemoryStream();
        await OctodiffPatch.Apply(new ForwardOnlyStream(new MemoryStream(basis)), new MemoryStream(delta), output, CancellationToken.None);

        Assert.True(target.AsSpan().SequenceEqual(output.ToArray()));
    }

    [Fact]
    public async Task Apply_AcceptsANonSeekableDelta()
    {
        var basis = Random(64 * 1024, 10);
        var target = Edit(basis);
        var delta = BuildDelta(basis, target);

        var output = new MemoryStream();
        await OctodiffPatch.Apply(new MemoryStream(basis), new ForwardOnlyStream(new MemoryStream(delta)), output, CancellationToken.None);

        Assert.True(target.AsSpan().SequenceEqual(output.ToArray()));
    }

    [Fact]
    public async Task Apply_WrapsACorruptedDeltaInPatchException()
    {
        var basis = Random(4096, 3);
        var delta = BuildDelta(basis, Edit(basis));
        // The delta starts with a magic header; breaking it makes Octodiff reject the stream.
        delta[0] ^= 0xFF;

        var exception = await Assert.ThrowsAsync<PatchException>(async () =>
            await OctodiffPatch.Apply(new MemoryStream(basis), new MemoryStream(delta), new MemoryStream(), CancellationToken.None));

        Assert.NotNull(exception.InnerException);
        Assert.Contains(exception.InnerException.Message, exception.Message);
    }

    [Fact]
    public async Task Apply_WrapsATruncatedDeltaInPatchException()
    {
        var basis = Random(4096, 4);
        var delta = BuildDelta(basis, Edit(basis));

        await Assert.ThrowsAsync<PatchException>(async () =>
            await OctodiffPatch.Apply(new MemoryStream(basis), new MemoryStream(delta, 0, delta.Length / 2), new MemoryStream(), CancellationToken.None));
    }

    [Fact]
    public async Task MergedPatch_EqualsPatchingTheConcatenatedSources()
    {
        var sources = new[] { Random(10_000, 5), Random(300_000, 6), Random(1, 7) };
        var joined = sources.SelectMany(s => s).ToArray();
        var target = Edit(joined);
        var delta = BuildDelta(joined, target);

        var output = new MemoryStream();
        await MergedPatch.Apply(sources.Select(s => (Stream)new MemoryStream(s)).ToList(), new MemoryStream(delta), output, CancellationToken.None);

        Assert.True(target.AsSpan().SequenceEqual(output.ToArray()));
    }

    [Fact]
    public async Task Apply_StopsWhenCancelled()
    {
        var basis = Random(20 * 1024 * 1024, 8);
        var delta = BuildDelta(basis, Edit(basis));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await OctodiffPatch.Apply(new MemoryStream(basis), new MemoryStream(delta), new MemoryStream(), cts.Token));
    }

    /// <summary>Cancels the token as soon as the apply starts producing output.</summary>
    private sealed class CancelOnWriteStream(CancellationTokenSource cts) : MemoryStream
    {
        public int Writes;

        public override void Write(byte[] buffer, int offset, int count)
        {
            Writes++;
            cts.Cancel();
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Writes++;
            cts.Cancel();
            base.Write(buffer);
        }
    }

    [Fact]
    public async Task Apply_StopsPartWayThroughALongApply()
    {
        var basis = Random(20 * 1024 * 1024, 9);
        var delta = BuildDelta(basis, Edit(basis));
        using var cts = new CancellationTokenSource();
        var output = new CancelOnWriteStream(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await OctodiffPatch.Apply(new MemoryStream(basis), new MemoryStream(delta), output, cts.Token));

        Assert.True(output.Length < basis.Length, "the apply kept writing after the token was cancelled");
    }
}
