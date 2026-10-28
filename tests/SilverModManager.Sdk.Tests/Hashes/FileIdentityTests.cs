using System.Buffers.Binary;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Sdk.Tests.Hashes;

public class FileIdentityTests
{
    private static byte[] Encode(FileIdentity identity)
    {
        var buffer = new byte[1 + FileIdentity.MaxDigestLength];
        var written = identity.Write(buffer);
        return buffer[..written];
    }

    [Fact]
    public void XxHash3AndSha1OfTheSameBytes_AreDifferentIdentities()
    {
        var hashes = MultiHasher.HashStream(new MemoryStream("vanilla"u8.ToArray())).Result;
        FileIdentity.XxHash3(hashes.XxHash3).Should().NotBe(FileIdentity.Sha1(hashes.Sha1));

        Span<byte> sameBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(sameBytes, hashes.XxHash3.Value);
        FileIdentity.XxHash3(hashes.XxHash3).Should().NotBe(FileIdentity.Of(HashKind.Sha1, sameBytes));
    }

    [Fact]
    public void Sha1Identity_RefusesToBeAnXxHash3()
    {
        var sha1 = FileIdentity.Sha1(Sha1Value.FromHex(new string('A', 40)));
        sha1.TryXxHash3(out _).Should().BeFalse();
        sha1.Invoking(i => i.XxHash3()).Should().Throw<InvalidOperationException>().WithMessage("*Sha1*");
    }

    [Fact]
    public void XxHash3Identity_GivesItsHashBack()
    {
        var identity = FileIdentity.XxHash3(Hash.From(0x0123456789ABCDEF));
        identity.TryXxHash3(out var hash).Should().BeTrue();
        hash.Should().Be(Hash.From(0x0123456789ABCDEF));
        identity.XxHash3().Should().Be(hash);
        identity.Kind.Should().Be(HashKind.XxHash3);
    }

    [Fact]
    public void EncodingRoundTrips_AndKeepsAKindThisBuildDoesNotKnow()
    {
        var future = FileIdentity.Of((HashKind)200, Enumerable.Range(1, 32).Select(b => (byte)b).ToArray());
        FileIdentity.Read(Encode(future)).Should().Be(future);
        FileIdentity.ParseHex(future.ToHex()).Should().Be(future);
        future.TryXxHash3(out _).Should().BeFalse();

        var tooLong = () => FileIdentity.Of((HashKind)200, new byte[FileIdentity.MaxDigestLength + 1]);
        tooLong.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void XxHash3Hex_IsTheKindByteThenSixteenBigEndianDigits()
        => FileIdentity.XxHash3(Hash.From(0x0123456789ABCDEF)).ToHex().Should().Be("010123456789ABCDEF");

    [Fact]
    public void Sha1Hex_IsTheKindByteThenTheDigestInStorageOrder()
    {
        var sha1 = Sha1Value.FromHex("0123456789ABCDEF0123456789ABCDEF01234567");
        FileIdentity.Sha1(sha1).ToHex().Should().Be("02" + Convert.ToHexString(sha1.AsSpan()));
    }

    [Fact]
    public void ParseHex_IsCaseInsensitive()
    {
        var identity = FileIdentity.XxHash3(Hash.From(0xABCDEF0123456789));
        FileIdentity.ParseHex(identity.ToHex().ToLowerInvariant()).Should().Be(identity);
    }

    [Fact]
    public void None_IsNotEqualToAnyIdentity_AndIsNone()
    {
        FileIdentity.None.IsNone.Should().BeTrue();
        FileIdentity.None.Should().Be(default(FileIdentity));
        FileIdentity.None.Should().NotBe(FileIdentity.XxHash3(Hash.From(0)));
        FileIdentity.XxHash3(Hash.From(0)).IsNone.Should().BeFalse();
    }

    [Fact]
    public void OfMultiHash_TakesTheDigestOfThatKind()
    {
        var hashes = MultiHasher.HashStream(new MemoryStream("vanilla"u8.ToArray())).Result;
        FileIdentity.Of(hashes, HashKind.XxHash3).Should().Be(FileIdentity.XxHash3(hashes.XxHash3));
        FileIdentity.Of(hashes, HashKind.Sha1).Should().Be(FileIdentity.Sha1(hashes.Sha1));
    }

    [Theory]
    [InlineData(HashKind.None)]
    [InlineData((HashKind)200)]
    public void OfMultiHash_RefusesNoneAndAKindMultiHashLacks(HashKind kind)
    {
        var hashes = MultiHasher.HashStream(new MemoryStream("vanilla"u8.ToArray())).Result;
        var act = () => FileIdentity.Of(hashes, kind);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ToString_NamesTheKind()
    {
        FileIdentity.XxHash3(Hash.From(0x0123456789ABCDEF)).ToString().Should().StartWith("xxh3:");
        FileIdentity.Sha1(Sha1Value.FromHex(new string('A', 40))).ToString().Should().StartWith("sha1:");
        FileIdentity.None.ToString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void EqualIdentities_HaveEqualHashCodes()
    {
        var a = FileIdentity.XxHash3(Hash.From(42));
        var b = FileIdentity.XxHash3(Hash.From(42));
        a.GetHashCode().Should().Be(b.GetHashCode());
        (a == b).Should().BeTrue();
        (a != FileIdentity.XxHash3(Hash.From(43))).Should().BeTrue();
    }
}
