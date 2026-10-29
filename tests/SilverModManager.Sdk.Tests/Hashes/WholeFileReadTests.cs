using System.Security.Cryptography;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Sdk.Tests.Hashes;

public class WholeFileReadTests
{
    private const int OneMiB = 1024 * 1024;

    /// <summary>A stream that remembers how many bytes each read asked for.</summary>
    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public List<int> RequestedSizes { get; } = [];

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            RequestedSizes.Add(buffer.Length);
            return base.ReadAsync(buffer, cancellationToken);
        }

        public override int Read(Span<byte> buffer)
        {
            RequestedSizes.Add(buffer.Length);
            return base.Read(buffer);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            RequestedSizes.Add(count);
            return base.Read(buffer, offset, count);
        }
    }

    private static byte[] Random(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public async Task HashingAMultiMegabyteStream_ReadsInBlocksOfAtLeastOneMegabyte()
    {
        var stream = new CountingStream(Random(3 * OneMiB + 17));

        await Xx3Hasher.HashAsync(stream, cancellationToken: TestContext.Current.CancellationToken);

        stream.RequestedSizes.Should().NotBeEmpty().And.OnlyContain(size => size >= OneMiB);
    }

    [Fact]
    public async Task HashingEveryDigestAtOnce_ReadsInBlocksOfAtLeastOneMegabyte()
    {
        var stream = new CountingStream(Random(3 * OneMiB + 17));

        await MultiHasherBuilder.Start()
            .AddHasher<Sha256Value, SHA256, Sha256Hasher>()
            .AddHasher<Hash, System.IO.Hashing.XxHash3, Xx3Hasher>()
            .HashAsync(stream, cancellationToken: TestContext.Current.CancellationToken);

        stream.RequestedSizes.Should().NotBeEmpty().And.OnlyContain(size => size >= OneMiB);
    }

    [Fact]
    public async Task ASmallStream_IsNotGivenAMegabyteBuffer()
    {
        var stream = new CountingStream(Random(100));

        await Xx3Hasher.HashAsync(stream, cancellationToken: TestContext.Current.CancellationToken);

        stream.RequestedSizes.Should().OnlyContain(size => size <= 100);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8 * 1024)]
    [InlineData(OneMiB - 1)]
    [InlineData(OneMiB)]
    [InlineData(OneMiB + 1)]
    [InlineData(3 * OneMiB + 17)]
    public async Task TheBlockSize_DoesNotChangeTheDigests(int length)
    {
        var bytes = Random(length);

        var multi = await MultiHasher.HashStream(new MemoryStream(bytes), TestContext.Current.CancellationToken);

        multi.XxHash3.Should().Be(Hash.From(System.IO.Hashing.XxHash3.HashToUInt64(bytes)));
        multi.XxHash64.Should().Be(Hash.From(System.IO.Hashing.XxHash64.HashToUInt64(bytes)));
        multi.Sha1.Should().Be(Sha1Value.From(SHA1.HashData(bytes)));
        multi.Md5.Should().Be(Md5Value.From(MD5.HashData(bytes)));
        multi.Size.Should().Be(Size.FromLong(length));
    }

    [Fact]
    public async Task TheSequentialOpener_ReadsTheFileSoTheDigestsMatch()
    {
        var bytes = Random(2 * OneMiB + 5);
        var path = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine("silver-sequential-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(path.GetFullPath(), bytes, TestContext.Current.CancellationToken);
        try
        {
            await using var stream = SequentialFileRead.Open(path);
            var multi = await MultiHasher.HashStream(stream, TestContext.Current.CancellationToken);

            multi.XxHash3.Should().Be(Hash.From(System.IO.Hashing.XxHash3.HashToUInt64(bytes)));
            multi.Size.Should().Be(Size.FromLong(bytes.Length));
        }
        finally
        {
            path.Delete();
        }
    }
}
