using FluentAssertions;
using SilverModManager.Backend.FileExtractor;

namespace SilverModManager.Backend.Tests.FileExtractor;

public class EntryNamesTests
{
    [Theory]
    [InlineData("foo/bar", "foo/bar")]
    [InlineData("foo/bar/", "foo/bar")]
    [InlineData("foo\\bar\\", "foo\\bar")]
    [InlineData("foo /bar ", "foo/bar")]
    [InlineData("foo./bar.", "foo/bar")]
    [InlineData("foo.\\bar.", "foo\\bar")]
    [InlineData("foo /bar /", "foo/bar")]
    [InlineData("foo/bar. /", "foo/bar")]
    public void Test_FixPath(string input, string expected)
    {
        EntryNames.FixPath(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("foo/bar.baz", "foo/bar.baz")]
    [InlineData("foo bar.baz", "foo bar.baz")]
    [InlineData("foo.bar.", "foo.bar")]
    [InlineData("foo/bar ", "foo/bar")]
    [InlineData("foo/bar .", "foo/bar")]
    [InlineData("foo/bar. ", "foo/bar")]
    public void Test_FixSegmentTrimsTrailingSpacesAndDots(string input, string expected)
    {
        EntryNames.FixSegment(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("...")]
    [InlineData(". .")]
    public void ASegmentWithNothingLeftBecomesAnUnderscore(string input)
    {
        EntryNames.FixSegment(input).Should().Be("_");
    }

    [Fact]
    public void ControlCharactersReplacementCharactersAndLoneSurrogatesBecomeUnderscores()
    {
        EntryNames.FixSegment("a\u0001b�c\uD800d.txt").Should().Be("a_b_c_d.txt");
        EntryNames.FixSegment("naïve_\U0001F600.txt").Should().Be("naïve_\U0001F600.txt", "a surrogate pair is a real character");
    }

    [Fact]
    public void AnEmptySegmentStaysEmpty()
    {
        EntryNames.FixSegment("").Should().BeEmpty();
    }

    [Fact]
    public void ValidUtf8IsDecodedAsUtf8()
    {
        EntryNames.Decode("naïve_日本.txt"u8).Should().Be("naïve_日本.txt");
    }

    // 0x8B and 0x94 are 'ï' and 'ö' in code page 437 and are not valid UTF-8.
    [Fact]
    public void BytesThatAreNotUtf8AreDecodedAsCodePage437()
    {
        EntryNames.Decode([(byte)'a', 0x8B, (byte)'b', 0x94]).Should().Be("aïbö");
    }

    [Fact]
    public void AMixedNameIsDecodedWholeNotPerSegment()
    {
        // A valid UTF-8 folder next to a CP437 file: the whole name is not UTF-8, so all of it is 437.
        EntryNames.Decode([0xC3, 0xB6, (byte)'/', 0x8B]).Should().Be("├╢/ï");
    }

    [Theory]
    [InlineData("plain.txt", true)]
    [InlineData("trailing ", true)]
    public void AValidUtf8NameIsAlwaysAddressable(string name, bool expected)
    {
        EntryNames.IsAddressable(System.Text.Encoding.UTF8.GetBytes(name)).Should().Be(expected);
    }

    [Fact]
    public void ANameThatIsNotUtf8AndEndsInWhitespaceCannotBeAddressed()
    {
        EntryNames.IsAddressable([0x8B, (byte)' ']).Should().BeFalse();
        EntryNames.IsAddressable([0x8B, (byte)'\t']).Should().BeFalse();
        EntryNames.IsAddressable([0x8B, (byte)'x']).Should().BeTrue();
        EntryNames.IsAddressable([0x8B, 0xA0]).Should().BeTrue("0xA0 is not ASCII whitespace");
    }

    [Fact]
    public void PrintableSpellsOutControlCharacters()
    {
        EntryNames.Printable("a\u0001b�").Should().Be("a\\u0001b\\uFFFD");
    }
}
