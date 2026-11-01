using FluentAssertions;
using SilverModManager.Platform.Proton.UserTools;

namespace SilverModManager.Platform.Proton.Tests.Tools;

public class ArgumentSplitterTests
{
    [Theory]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("-a -b", new[] { "-a", "-b" })]
    [InlineData("  -a   -b  ", new[] { "-a", "-b" })]
    [InlineData("\"two words\" x", new[] { "two words", "x" })]
    [InlineData("-o:\"C:\\My Dir\"", new[] { "-o:C:\\My Dir" })]
    [InlineData("\"\" x", new[] { "", "x" })]
    [InlineData("\"unterminated word", new[] { "unterminated word" })]
    public void ArgumentSplitter_SplitsOnSpacesAndGroupsDoubleQuotedWords(string input, string[] expected)
    {
        ArgumentSplitter.Split(input).Should().Equal(expected);
    }
}
