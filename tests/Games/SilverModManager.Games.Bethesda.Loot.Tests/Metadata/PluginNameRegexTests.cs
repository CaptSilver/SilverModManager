using NexusMods.Games.Bethesda.Loot.Metadata;

namespace NexusMods.Games.Bethesda.Loot.Tests.Metadata;

// Ports of the case_insensitive_regex tests in libloot's src/tests.rs that .NET's ECMAScript mode
// agrees with. libloot runs the pattern through regress in unicode mode, which also rejects
// identity escapes (\q), octal escapes, POSIX classes, \u{...} and bare brackets; .NET accepts
// those, so the rejection tests are not ported. Every name here contains one of the regex
// characters so that PluginName treats it as a pattern.
public class PluginNameRegexTests
{
    [Fact]
    public void ShouldBeCaseInsensitive()
    {
        new PluginName("A|B").Matches("a").Should().BeTrue();
    }

    [Fact]
    public void ShouldSupportTheIModifier()
    {
        var name = new PluginName("(?-i:A)|x");

        name.Matches("A").Should().BeTrue();
        name.Matches("a").Should().BeFalse();
    }

    [Fact]
    public void ShouldSupportTheSModifier()
    {
        new PluginName("A(?s:.)B|x").Matches("A\nB").Should().BeTrue();
    }

    [Theory]
    [InlineData('$')]
    [InlineData('(')]
    [InlineData(')')]
    [InlineData('*')]
    [InlineData('+')]
    [InlineData('.')]
    [InlineData('/')]
    [InlineData('?')]
    [InlineData('[')]
    [InlineData('\\')]
    [InlineData(']')]
    [InlineData('^')]
    [InlineData('{')]
    [InlineData('|')]
    [InlineData('}')]
    public void ShouldSupportSyntaxCharacterEscapes(char c)
    {
        new PluginName($"\\{c}").Matches(c.ToString()).Should().BeTrue();
    }

    [Fact]
    public void ShouldSupportNullEscape()
    {
        new PluginName("\\0").Matches("\0").Should().BeTrue();
    }

    [Fact]
    public void ShouldSupportControlCharacterEscapes()
    {
        new PluginName("\\cJ").Matches("\n").Should().BeTrue();
    }

    [Fact]
    public void ShouldNotSupportDigitControlCharacterEscapesInCharacterClasses()
    {
        var act = () => new PluginName("[\\c0]");

        act.Should().Throw<InvalidPluginNameException>();
    }

    [Fact]
    public void ShouldNotSupportBackslashKWhenThereAreNoCapturingGroups()
    {
        var act = () => new PluginName("\\k<name>");

        act.Should().Throw<InvalidPluginNameException>();
    }

    [Fact]
    public void ShouldNotSupportABareBackslashCAtTheEndOfThePattern()
    {
        var act = () => new PluginName("\\c");

        act.Should().Throw<InvalidPluginNameException>();
    }
}
