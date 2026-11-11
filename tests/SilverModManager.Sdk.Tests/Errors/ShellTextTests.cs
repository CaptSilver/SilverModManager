using FluentAssertions;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Sdk.Tests.Errors;

public class ShellTextTests
{
    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("", "''")]
    [InlineData("a b", "'a b'")]
    [InlineData("it's", @"'it'\''s'")]
    [InlineData("$(touch x) `id` \"q\"", "'$(touch x) `id` \"q\"'")]
    public void QuoteWrapsTheValueInSingleQuotes(string value, string expected)
        => ShellText.Quote(value).Should().Be(expected);

    // Steam substitutes a literal %command% in a launch-option line before the shell sees it.
    [Fact]
    public void QuoteSplitsALiteralCommandTokenSoSteamCannotSubstituteIt()
        => ShellText.Quote("a %command% b").Should().Be("'a %comm''and% b'");
}
