using SilverModManager.Games.Bethesda.Loot.Metadata;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Metadata;

// Test names follow libloot's metadata/plugin_metadata.rs (replace_capturing_groups) and the
// regex section of its src/tests.rs.
public class PluginNameTests
{
    public class ReplaceCapturingGroupsTests
    {
        [Fact]
        public void ShouldReplaceCapturingGroupsWithNonCapturingGroups()
        {
            var output = PluginName.ReplaceCapturingGroups(@"(a)?\((b)\)((?x)|\\(d))(?:e)\(");

            output.Should().Be(@"(?:a)?\((?:b)\)(?:(?x)|\\(?:d))(?:e)\(");
        }

        [Fact]
        public void ShouldNotChangeStringIfNoOpeningParenthesesAreFound()
        {
            PluginName.ReplaceCapturingGroups("no parentheses").Should().Be("no parentheses");
        }

        [Fact]
        public void ShouldNotChangeStringIfNoCapturingGroupsAreFound()
        {
            PluginName.ReplaceCapturingGroups("no paren(?:thes)es").Should().Be("no paren(?:thes)es");
            PluginName.ReplaceCapturingGroups("no paren(?:th(?:e)s)es").Should().Be("no paren(?:th(?:e)s)es");
        }

        [Fact]
        public void ShouldCloneNonCapturingPrefixCorrectly()
        {
            var output = PluginName.ReplaceCapturingGroups(@"(?:a)?\(?:(b)\)((?x)|\\(d))(?:e)\(");

            output.Should().Be(@"(?:a)?\(?:(?:b)\)(?:(?x)|\\(?:d))(?:e)\(");
        }

        [Fact]
        public void ShouldHandleAnEmptyStringAndATrailingParenthesis()
        {
            PluginName.ReplaceCapturingGroups("").Should().BeEmpty();
            PluginName.ReplaceCapturingGroups("a(").Should().Be("a(");
            PluginName.ReplaceCapturingGroups("a((").Should().Be("a(?:(");
        }
    }

    public class TrimDotGhostTests
    {
        [Theory]
        [InlineData("Blank.esp.ghost", "Blank.esp")]
        [InlineData("Blank.esp.GHOST", "Blank.esp")]
        [InlineData("Blank.esp.Ghost", "Blank.esp")]
        [InlineData("Blank.esp", "Blank.esp")]
        [InlineData(".ghost", "")]
        [InlineData("ghost", "ghost")]
        [InlineData("", "")]
        [InlineData("Blank.esp.ghosts", "Blank.esp.ghosts")]
        public void ShouldRemoveATrailingGhostExtension(string input, string expected)
        {
            PluginName.TrimDotGhost(input).Should().Be(expected);
        }

        [Fact]
        public void ShouldOnlyFoldAsciiCase()
        {
            // U+017F (long s) upper-cases to 'S', so a culture-aware or OrdinalIgnoreCase check would trim this.
            PluginName.TrimDotGhost("Blank.esp.ghoſt").Should().Be("Blank.esp.ghoſt");
        }
    }

    public class IsRegexNameTests
    {
        [Theory]
        [InlineData("Blank.esm", false)]
        [InlineData("", false)]
        [InlineData("Blank (1).esm", false)]
        [InlineData("a:b", true)]
        [InlineData("a\\b", true)]
        [InlineData("a*", true)]
        [InlineData("a?", true)]
        [InlineData("a|b", true)]
        public void ShouldBeTrueOnlyForTheFiveRegexCharacters(string name, bool expected)
        {
            PluginName.IsRegexName(name).Should().Be(expected);
        }
    }

    public class MatchesTests
    {
        [Fact]
        public void ShouldAnchorTheNameAsWrittenSoAlternationsBindLooserThanTheAnchors()
        {
            // ^a|b$ is (^a)|(b$): it matches anything starting with a or ending with b, which
            // ^(?:a|b)$ would not.
            var name = new PluginName("a|b");

            name.Matches("abc").Should().BeTrue("it starts with a");
            name.Matches("cab").Should().BeTrue("it ends with b");
            name.Matches("cac").Should().BeFalse();
        }

        [Fact]
        public void ShouldMatchTheWholeFilename()
        {
            var name = new PluginName("Blank.es(m|p)?");

            name.Matches("Blank.es").Should().BeTrue();
            name.Matches("Blank.esp").Should().BeTrue();
            name.Matches("xBlank.esp").Should().BeFalse();
            name.Matches("Blank.espx").Should().BeFalse();
        }

        [Fact]
        public void ShouldStripDotGhostBeforeDecidingWhetherTheNameIsARegex()
        {
            var name = new PluginName("Blank.esp.ghost");

            name.IsRegex.Should().BeFalse();
            name.Value.Should().Be("Blank.esp");
        }

        [Fact]
        public void ShouldBeCaseInsensitive()
        {
            new PluginName("A").Matches("a").Should().BeTrue();
            new PluginName("A|B").Matches("b").Should().BeTrue();
        }

        [Fact]
        public void ShouldSupportQuantifiedAndAlternatedGroups()
        {
            var name = new PluginName(@"Blank( - \d+)?\.es(m|p)");

            name.Matches("Blank.esm").Should().BeTrue();
            name.Matches("blank - 12.ESP").Should().BeTrue();
            name.Matches("Blank - x.esp").Should().BeFalse();
        }

        [Fact]
        public void ShouldNotTreatACapturingGroupAsCapturing()
        {
            // A backreference to a replaced group can never match: libloot has no use for captures.
            var act = () => new PluginName(@"(a)\1|b");

            act.Should().NotThrow();
        }
    }

    [Fact]
    public void ShouldCompareByNameTextCaseSensitively()
    {
        new PluginName("a").Should().Be(new PluginName("a"));
        new PluginName("a").Should().NotBe(new PluginName("A"));
        new PluginName("a|b").GetHashCode().Should().Be(new PluginName("a|b").GetHashCode());
    }

    [Fact]
    public void ShouldWrapAnInvalidRegexInAnInvalidPluginNameException()
    {
        var act = () => new PluginName("(a|");

        act.Should().Throw<InvalidPluginNameException>().WithInnerException<ArgumentException>();
    }
}
