using System.Text;
using FluentAssertions;
using SilverModManager.Platform.Proton.ToolRunner;

namespace SilverModManager.Platform.Proton.Tests;

public class OutputTailTests
{
    private static async Task<string> Tail(params byte[][] chunks)
    {
        var tail = new OutputTail();
        await tail.CopyFromAsync(new ChunkedStream(chunks));
        return tail.Text;
    }

    [Fact]
    public async Task KeepsOnlyTheLastCapacityBytes()
    {
        var source = string.Concat(Enumerable.Range(0, 3 * OutputTail.Capacity / 8).Select(i => $"line {i}\n"));
        var text = await Tail(Encoding.UTF8.GetBytes(source));

        text.Length.Should().BeLessThanOrEqualTo(OutputTail.Capacity);
        text.Should().EndWith($"line {3 * OutputTail.Capacity / 8 - 1}\n");
    }

    [Fact]
    public async Task StartsAtALineBoundaryAfterDroppingBytes()
    {
        var source = string.Concat(Enumerable.Range(0, 5000).Select(i => $"line {i}\n"));
        var text = await Tail(Encoding.UTF8.GetBytes(source));

        text.Should().StartWith("line ");
        source.Should().Contain("\n" + text);
    }

    [Fact]
    public async Task AMultiByteCharacterSplitAcrossChunksIsIntact()
    {
        var text = await Tail("caf"u8.ToArray().Append((byte)0xC3).ToArray(), [0xA9, (byte)'\n']);
        text.Should().Be("café\n");
    }

    [Fact]
    public async Task AnEmptyStreamGivesEmptyText() => (await Tail()).Should().BeEmpty();

    [Fact]
    public async Task NothingIsDroppedWhileTheStreamFitsSoTheFirstLineIsKept()
        => (await Tail("a\nb\n"u8.ToArray())).Should().Be("a\nb\n");

    // Returns one chunk per read so the tests control where a read ends.
    private sealed class ChunkedStream(byte[][] chunks) : Stream
    {
        private int _index;
        private int _consumed;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_index >= chunks.Length) return 0;
            var chunk = chunks[_index];
            var n = Math.Min(count, chunk.Length - _consumed);
            Array.Copy(chunk, _consumed, buffer, offset, n);
            _consumed += n;
            if (_consumed == chunk.Length) { _index++; _consumed = 0; }
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
