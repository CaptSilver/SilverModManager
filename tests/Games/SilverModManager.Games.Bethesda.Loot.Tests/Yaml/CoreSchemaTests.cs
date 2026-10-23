using SilverModManager.Games.Bethesda.Loot.Metadata.Yaml;
using YamlDotNet.RepresentationModel;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Yaml;

// saphyr resolves plain scalars under the YAML 1.2 core schema and libloot's typed getters read
// that result; YamlDotNet's representation model only returns strings.
public class CoreSchemaTests
{
    private static ScalarKind Classify(string yaml, out long integer)
    {
        var node = (YamlScalarNode)TestYaml.Parse(yaml);
        return CoreSchema.Classify(node, out integer);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("42", 42)]
    [InlineData("-42", -42)]
    [InlineData("+42", 42)]
    [InlineData("0x12AB", 0x12AB)]
    [InlineData("0xdeadbeef", 0xDEADBEEF)]
    [InlineData("0xDEADBEEF", 0xDEADBEEF)]
    [InlineData("0o17", 15)]
    [InlineData("3735928559", 3735928559)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    public void PlainIntegersShouldResolveInDecimalHexAndOctal(string text, long expected)
    {
        Classify(text, out var integer).Should().Be(ScalarKind.Integer);
        integer.Should().Be(expected);
    }

    [Theory]
    [InlineData("'42'")]
    [InlineData("\"42\"")]
    [InlineData("'0x12AB'")]
    [InlineData("'true'")]
    [InlineData("'~'")]
    public void QuotedScalarsShouldAlwaysBeStrings(string yaml)
    {
        Classify(yaml, out _).Should().Be(ScalarKind.String);
    }

    [Theory]
    [InlineData("~")]
    [InlineData("null")]
    [InlineData("NULL")]
    public void PlainNullSpellingsShouldResolveToNull(string text)
    {
        Classify(text, out _).Should().Be(ScalarKind.Null);
    }

    [Fact]
    public void AnEmptyPlainValueShouldBeNull()
    {
        var node = (YamlScalarNode)((YamlMappingNode)TestYaml.Parse("{name: }")).Children.Values.Single();

        CoreSchema.Classify(node, out _).Should().Be(ScalarKind.Null);
    }

    [Theory]
    [InlineData("true", "Boolean")]
    [InlineData("false", "Boolean")]
    [InlineData("True", "String")]
    [InlineData("TRUE", "String")]
    [InlineData("Null", "String")]
    [InlineData("1.5", "Float")]
    [InlineData("-1.5e3", "Float")]
    [InlineData(".5", "Float")]
    [InlineData("5.", "Float")]
    [InlineData(".inf", "Float")]
    [InlineData("-.INF", "Float")]
    [InlineData(".NaN", "Float")]
    [InlineData("inf", "String")]
    [InlineData("nan", "String")]
    [InlineData("Blank.esp", "String")]
    [InlineData("1.2.esp", "String")]
    [InlineData("0x", "String")]
    [InlineData("0xZZ", "String")]
    [InlineData("0o9", "String")]
    [InlineData("1_000", "String")]
    [InlineData("-0x10", "String")]
    [InlineData("99999999999999999999", "Float")]
    public void PlainScalarsShouldFollowTheCoreSchemaOrder(string text, string expected)
    {
        Classify(text, out _).ToString().Should().Be(expected);
    }
}
