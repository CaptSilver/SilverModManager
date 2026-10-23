using System.Security.Cryptography;
using FluentAssertions;
using NexusMods.Sdk.Hashes;

namespace NexusMods.Sdk.Tests.Hashes;

public class Sha256Tests
{
    private static readonly string KnownHex = Convert.ToHexString(SHA256.HashData("abc"u8)).ToLowerInvariant();

    [Fact]
    public void UppercaseHexParsesToTheSameValue()
    {
        Sha256Value.TryParse(KnownHex, out var lower).Should().BeTrue();
        Sha256Value.TryParse(KnownHex.ToUpperInvariant(), out var upper).Should().BeTrue();
        upper.Should().Be(lower);
        lower.ToString().Should().Be(KnownHex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zz7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad00")]
    public void TryParseRejectsAnythingButSixtyFourHexChars(string input)
    {
        Sha256Value.TryParse(input, out _).Should().BeFalse();
    }

    [Fact]
    public async Task HasherMatchesTheFrameworkDigest()
    {
        var bytes = "hello world"u8.ToArray();
        var actual = await Sha256Hasher.HashAsync(new MemoryStream(bytes), cancellationToken: TestContext.Current.CancellationToken);
        actual.AsSpan().ToArray().Should().Equal(SHA256.HashData(bytes));
    }

    [Fact]
    public void EmptyExpectedNeverMatches()
    {
        Sha256Match.Matches(Sha256Value.From(SHA256.HashData("abc"u8)), "").Should().BeFalse();
        Sha256Match.Matches(Sha256Value.From(SHA256.HashData("abc"u8)), null).Should().BeFalse();
        Sha256Match.Matches(Sha256Value.From(SHA256.HashData("abc"u8)), "not hex").Should().BeFalse();
    }

    [Fact]
    public void MissingActualNeverMatches()
    {
        Sha256Match.Matches(null, KnownHex).Should().BeFalse();
        Sha256Match.Matches(null, "").Should().BeFalse();
        Sha256Match.Matches(null, null).Should().BeFalse();
    }

    [Fact]
    public void EqualDigestsMatchInEitherCase()
    {
        var actual = Sha256Value.From(SHA256.HashData("abc"u8));
        Sha256Match.Matches(actual, KnownHex).Should().BeTrue();
        Sha256Match.Matches(actual, KnownHex.ToUpperInvariant()).Should().BeTrue();
        Sha256Match.Matches(Sha256Value.From(SHA256.HashData("abd"u8)), KnownHex).Should().BeFalse();
    }
}
