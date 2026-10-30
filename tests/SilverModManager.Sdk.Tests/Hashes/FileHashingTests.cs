using System.Security.Cryptography;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Sdk.Tests.Hashes;

/// <summary>
/// <see cref="FileHashing"/> is the one door every file hash in <c>src</c> goes through, so these tests count what the
/// disk is asked for: a read smaller than a megabyte is what cut hashing to a fraction of the disk's speed.
/// </summary>
public class FileHashingTests
{
    private const int OneMiB = 1024 * 1024;
    private const int MinimalHashBlock = 64 * 1024;

    private readonly CountingFileSystem _fileSystem = new();
    private byte[] Write(string name, int length, out AbsolutePath path)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        path = _fileSystem.FromUnsanitizedFullPath("/hashing/" + name);
        path.Parent.CreateDirectory();
        using (var stream = path.Create()) stream.Write(bytes);
        _fileSystem.RequestedSizes.Clear();
        return bytes;
    }

    [Fact]
    public async Task XxHash3_ReadsTheFileInBlocksOfAtLeastOneMegabyte()
    {
        var bytes = Write("big.bin", 5 * OneMiB + 123, out var path);

        var hash = await FileHashing.XxHash3(path, TestContext.Current.CancellationToken);

        hash.Should().Be(Hash.From(System.IO.Hashing.XxHash3.HashToUInt64(bytes)));
        _fileSystem.RequestedSizes.Should().NotBeEmpty().And.OnlyContain(size => size >= OneMiB);
    }

    [Fact]
    public async Task HashAll_ReadsTheFileInBlocksOfAtLeastOneMegabyte()
    {
        var bytes = Write("big.bin", 3 * OneMiB + 5, out var path);

        var all = await FileHashing.HashAll(path, TestContext.Current.CancellationToken);

        all.XxHash3.Should().Be(Hash.From(System.IO.Hashing.XxHash3.HashToUInt64(bytes)));
        all.Size.Should().Be(Size.FromLong(bytes.Length));

        // The minimal hash is defined as three sampled 64 KB blocks, so those are the only small reads.
        var sizes = _fileSystem.RequestedSizes;
        sizes.Where(size => size < OneMiB).Should().OnlyContain(size => size == MinimalHashBlock).And.HaveCountLessThanOrEqualTo(3);
        sizes.Where(size => size >= OneMiB).Sum(size => (long)size).Should().BeGreaterThanOrEqualTo(bytes.Length);
    }

    [Fact]
    public async Task Sha256_ReadsTheFileInBlocksOfAtLeastOneMegabyte()
    {
        var bytes = Write("big.bin", 2 * OneMiB + 9, out var path);

        var digest = await FileHashing.Sha256(path, TestContext.Current.CancellationToken);

        digest.Should().Be(Sha256Value.From(SHA256.HashData(bytes)));
        _fileSystem.RequestedSizes.Should().NotBeEmpty().And.OnlyContain(size => size >= OneMiB);
    }

    [Fact]
    public async Task Md5_ReadsTheFileInBlocksOfAtLeastOneMegabyte()
    {
        var bytes = Write("big.bin", 2 * OneMiB + 9, out var path);

        var digest = await FileHashing.Md5(path, TestContext.Current.CancellationToken);

        digest.Should().Be(Md5Value.From(MD5.HashData(bytes)));
        _fileSystem.RequestedSizes.Should().NotBeEmpty().And.OnlyContain(size => size >= OneMiB);
    }

    [Fact]
    public async Task Read_HandsTheCallerAStreamThatReadsInBlocksOfAtLeastOneMegabyte()
    {
        Write("big.bin", 3 * OneMiB, out var path);

        var length = await FileHashing.Read(path, async (stream, token) =>
        {
            var hash = await Xx3Hasher.HashAsync(stream, cancellationToken: token);
            return (hash, stream.Length);
        }, TestContext.Current.CancellationToken);

        length.Length.Should().Be(3 * OneMiB);
        _fileSystem.RequestedSizes.Should().NotBeEmpty().And.OnlyContain(size => size >= OneMiB);
    }

    [Fact]
    public async Task AFileShorterThanABlock_IsReadWithoutAMegabyteBuffer()
    {
        var bytes = Write("small.bin", 100, out var path);

        var hash = await FileHashing.XxHash3(path, TestContext.Current.CancellationToken);

        hash.Should().Be(Hash.From(System.IO.Hashing.XxHash3.HashToUInt64(bytes)));
        _fileSystem.RequestedSizes.Should().OnlyContain(size => size <= 100);
    }

    [Fact]
    public async Task AnEmptyFile_HashesToTheEmptyDigest()
    {
        Write("empty.bin", 0, out var path);

        var hash = await FileHashing.XxHash3(path, TestContext.Current.CancellationToken);

        hash.Should().Be(Hash.From(System.IO.Hashing.XxHash3.HashToUInt64([])));
    }

    /// <summary>An in-memory file system whose streams remember how many bytes each read asked for.</summary>
    private sealed class CountingFileSystem() : InMemoryFileSystem(OSInformation.Shared)
    {
        public List<int> RequestedSizes { get; } = [];

        protected override Stream InternalOpenFile(AbsolutePath path, FileMode mode, FileAccess access, FileShare share)
            => new CountingStream(base.InternalOpenFile(path, mode, access, share), RequestedSizes);
    }

    private sealed class CountingStream(Stream inner, List<int> sizes) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (sizes) sizes.Add(count);
            return inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            lock (sizes) sizes.Add(buffer.Length);
            return inner.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (sizes) sizes.Add(buffer.Length);
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            lock (sizes) sizes.Add(count);
            return inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
