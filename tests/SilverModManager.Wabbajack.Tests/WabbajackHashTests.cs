using FluentAssertions;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.Wabbajack.Tests;

public class WabbajackHashTests
{
    [Theory]
    [InlineData("PfX6zen7m2M=")]
    [InlineData("83fJglT61Dk=")]
    public void ParseThenToBase64RoundTrips(string base64)
    {
        WabbajackHash.ToBase64(WabbajackHash.Parse(base64)).Should().Be(base64);
    }

    [Fact]
    public void WabbajackHash_RoundTripsAnXxHash64Identity()
    {
        var identity = WabbajackHash.Parse("PfX6zen7m2M=");

        identity.Kind.Should().Be(HashKind.XxHash64);
        identity.Digest.Length.Should().Be(8);
        WabbajackHash.ToBase64(identity).Should().Be("PfX6zen7m2M=");
    }

    [Fact]
    public void ToBase64_RefusesAnIdentityOfAnotherKind()
    {
        var act = () => WabbajackHash.ToBase64(FileIdentity.XxHash3(NexusMods.Hashing.xxHash3.Hash.From(1)));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TheEightBytesAreLittleEndian()
    {
        // 01 00 00 00 00 00 00 00 is 1 only when the first byte is the least significant one.
        var hash = WabbajackHash.Parse(Convert.ToBase64String([1, 0, 0, 0, 0, 0, 0, 0]));
        hash.TryXxHash64(out var xxHash64).Should().BeTrue();
        xxHash64.Value.Should().Be(1UL);

        WabbajackHash.ToBase64(FileIdentity.XxHash64(NexusMods.Hashing.xxHash3.Hash.From(0x0807060504030201UL)))
            .Should().Be(Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAAA")]
    [InlineData("not base64!")]
    public void ParseRejectsInputThatIsNotEightBytesOfBase64(string input)
    {
        var act = () => WabbajackHash.Parse(input);
        act.Should().Throw<FormatException>();
    }
}
