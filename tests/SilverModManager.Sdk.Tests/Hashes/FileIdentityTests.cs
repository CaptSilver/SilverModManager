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

    private static MultiHash HashesOf(byte[] bytes) => MultiHasher.HashStream(new MemoryStream(bytes)).Result;

    [Theory]
    [InlineData(HashKind.XxHash3, HashKind.XxHash3, true, IdentityMatch.Same)]
    [InlineData(HashKind.XxHash3, HashKind.XxHash3, false, IdentityMatch.Different)]
    [InlineData(HashKind.XxHash3, HashKind.Sha1, true, IdentityMatch.Unknown)]
    [InlineData(HashKind.Sha1, HashKind.Sha1, true, IdentityMatch.Same)]
    public void MatchSameKind_TruthTable(HashKind leftKind, HashKind rightKind, bool sameBytes, IdentityMatch expected)
    {
        var bytes = "the same bytes"u8.ToArray();
        var other = "other bytes"u8.ToArray();
        var left = FileIdentity.Of(HashesOf(bytes), leftKind);
        var right = FileIdentity.Of(HashesOf(sameBytes ? bytes : other), rightKind);
        left.MatchSameKind(right).Should().Be(expected);
    }

    [Fact]
    public void MatchSameKind_None_MatchesOnlyNone()
    {
        FileIdentity.None.MatchSameKind(FileIdentity.None).Should().Be(IdentityMatch.Same);
        FileIdentity.None.MatchSameKind(FileIdentity.XxHash3(Hash.From(1))).Should().Be(IdentityMatch.Different);
        FileIdentity.Sha1(Sha1Value.From(new byte[20])).MatchSameKind(FileIdentity.None).Should().Be(IdentityMatch.Different);
    }

    [Fact]
    public void Equals_AcrossKinds_IsFalseAndDoesNotThrow()
    {
        var xxh3 = FileIdentity.XxHash3(Hash.From(1));
        var sha1 = FileIdentity.Sha1(Sha1Value.From(new byte[20]));
        xxh3.Equals(sha1).Should().BeFalse();
        new HashSet<FileIdentity> { xxh3, sha1 }.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(32)]
    public void EqualsAndHashCode_AgreeWithByteCompare_ForEveryLength(int length)
    {
        var a = new byte[length];
        Random.Shared.NextBytes(a);
        var b = a.ToArray();
        b[^1] ^= 1;
        var ia = FileIdentity.Of(HashKind.Crc32, a);
        var ia2 = FileIdentity.Of(HashKind.Crc32, a);
        var ib = FileIdentity.Of(HashKind.Crc32, b);
        ia.Equals(ia2).Should().BeTrue();
        ia.GetHashCode().Should().Be(ia2.GetHashCode());
        ia.Equals(ib).Should().BeFalse();
    }

    [Fact]
    public void EveryFactoryRoundTripsThroughItsExtractor()
    {
        var hashes = MultiHasher.HashStream(new MemoryStream("round trip"u8.ToArray())).Result;

        FileIdentity.Md5(hashes.Md5).TryMd5(out var md5).Should().BeTrue();
        md5.Should().Be(hashes.Md5);

        FileIdentity.Crc32(hashes.Crc32).TryCrc32(out var crc32).Should().BeTrue();
        crc32.Should().Be(hashes.Crc32);

        FileIdentity.XxHash64(hashes.XxHash64).TryXxHash64(out var xxHash64).Should().BeTrue();
        xxHash64.Should().Be(hashes.XxHash64);

        FileIdentity.Sha1(hashes.Sha1).TrySha1(out var sha1).Should().BeTrue();
        sha1.Should().Be(hashes.Sha1);
    }

    [Fact]
    public void AnExtractorRefusesAnotherKind()
    {
        var xxh3 = FileIdentity.XxHash3(Hash.From(1));
        xxh3.TrySha1(out _).Should().BeFalse();
        xxh3.TryMd5(out _).Should().BeFalse();
        xxh3.TryCrc32(out _).Should().BeFalse();
        xxh3.TryXxHash64(out _).Should().BeFalse("an xxHash64 and an xxHash3 are both eight bytes but not the same name");
        FileIdentity.XxHash64(Hash.From(1)).TryXxHash3(out _).Should().BeFalse();
        FileIdentity.Sha1(Sha1Value.From(new byte[20])).TryCrc32(out _).Should().BeFalse();
    }

    [Fact]
    public void AnExtractorRefusesTheRightKindAtTheWrongLength()
    {
        FileIdentity.Of(HashKind.Sha1, new byte[19]).TrySha1(out _).Should().BeFalse();
        FileIdentity.Of(HashKind.Md5, new byte[15]).TryMd5(out _).Should().BeFalse();
        FileIdentity.Of(HashKind.Crc32, new byte[5]).TryCrc32(out _).Should().BeFalse();
        FileIdentity.Of(HashKind.XxHash64, new byte[7]).TryXxHash64(out _).Should().BeFalse();
    }

    [Fact]
    public void Sha256Identity_HasThirtyTwoBytesAndItsOwnName()
    {
        var identity = FileIdentity.Sha256(Sha256Value.From(new byte[32]));
        identity.Kind.Should().Be(HashKind.Sha256);
        identity.Digest.Length.Should().Be(32);
        identity.ToString().Should().StartWith("sha256:");
    }

    [Theory]
    [InlineData(HashKind.XxHash3)]
    [InlineData(HashKind.Sha1)]
    [InlineData(HashKind.Md5)]
    [InlineData(HashKind.XxHash64)]
    [InlineData(HashKind.Crc32)]
    public void MultiHashIdentity_MatchesTheFactoryForThatKind(HashKind kind)
    {
        var hashes = MultiHasher.HashStream(new MemoryStream("multi"u8.ToArray())).Result;
        var expected = kind switch
        {
            HashKind.XxHash3 => FileIdentity.XxHash3(hashes.XxHash3),
            HashKind.Sha1 => FileIdentity.Sha1(hashes.Sha1),
            HashKind.Md5 => FileIdentity.Md5(hashes.Md5),
            HashKind.XxHash64 => FileIdentity.XxHash64(hashes.XxHash64),
            HashKind.Crc32 => FileIdentity.Crc32(hashes.Crc32),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        hashes.Identity(kind).Should().Be(expected);
        hashes.Identity(kind).Kind.Should().Be(kind);
    }
}
