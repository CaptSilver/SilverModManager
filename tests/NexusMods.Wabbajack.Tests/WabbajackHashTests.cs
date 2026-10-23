using FluentAssertions;
using NexusMods.Wabbajack.Format;

namespace NexusMods.Wabbajack.Tests;

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
    public void TheEightBytesAreLittleEndian()
    {
        // 01 00 00 00 00 00 00 00 is 1 only when the first byte is the least significant one.
        var hash = WabbajackHash.Parse(Convert.ToBase64String([1, 0, 0, 0, 0, 0, 0, 0]));
        hash.Value.Should().Be(1UL);

        WabbajackHash.ToBase64(NexusMods.Hashing.xxHash3.Hash.From(0x0807060504030201UL))
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
