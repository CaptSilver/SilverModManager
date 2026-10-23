using SilverModManager.Games.Bethesda.Loot.Metadata;
using SilverModManager.Games.Bethesda.Loot.Metadata.Yaml;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Yaml;

// libloot's should_error_if_given_an_invalid_condition and ..._constraint cases for File, Tag and
// Message, plus the cleaning data condition.
public class ConditionValidationTests
{
    private static Action Reading<T>(Func<YamlDotNet.RepresentationModel.YamlNode, T> read, string yaml) =>
        () => read(TestYaml.Parse(yaml));

    [Fact]
    public void ShouldErrorIfAFileHasAnInvalidCondition() =>
        Reading(MetadataYaml.ReadFile, "{name: name1, condition: invalid}")
            .Should().Throw<MetadataParseException>().WithMessage("*the condition string \"invalid\" is invalid");

    [Fact]
    public void ShouldErrorIfAFileHasAnInvalidConstraint() =>
        Reading(MetadataYaml.ReadFile, "{name: name1, constraint: bad}")
            .Should().Throw<MetadataParseException>().WithMessage("*the condition string \"bad\" is invalid");

    [Fact]
    public void ShouldErrorIfATagHasAnInvalidCondition() =>
        Reading(MetadataYaml.ReadTag, "{name: Relev, condition: invalid}")
            .Should().Throw<MetadataParseException>();

    [Fact]
    public void ShouldErrorIfAMessageHasAnInvalidCondition() =>
        Reading(MetadataYaml.ReadMessage, "{type: say, content: text, condition: invalid}")
            .Should().Throw<MetadataParseException>();

    [Fact]
    public void ShouldErrorIfCleaningDataHasAnInvalidCondition() =>
        Reading(MetadataYaml.ReadPluginCleaningData, "{crc: 0x12345678, util: cleaner, condition: invalid}")
            .Should().Throw<MetadataParseException>().WithMessage("*the condition string \"invalid\" is invalid");

    [Fact]
    public void ShouldNameCleaningDataWhenItsConditionIsNotAString() =>
        Reading(MetadataYaml.ReadPluginCleaningData, "{crc: 0x12345678, util: cleaner, condition: [a]}")
            .Should().Throw<MetadataParseException>().WithMessage("*\"condition\" key in \"plugin cleaning data\" map*");

    [Fact]
    public void ShouldErrorIfAConditionUsesARegexLootRefuses() =>
        Reading(MetadataYaml.ReadFile, "{name: name1, condition: 'file(\"a(?=b)\")'}")
            .Should().Throw<MetadataParseException>();

    [Fact]
    public void ShouldKeepAValidConditionAsWritten()
    {
        var file = MetadataYaml.ReadFile(TestYaml.Parse("{name: n, condition: 'file(\"a.esp\") and not active(\"b.esp\")'}"));

        file.Condition.Should().Be("file(\"a.esp\") and not active(\"b.esp\")");
    }

    [Fact]
    public void ShouldReportTheLocationOfTheConditionValue()
    {
        var thrown = Reading(MetadataYaml.ReadFile, "{name: n,\n condition: invalid}")
            .Should().Throw<MetadataParseException>().Which;

        thrown.Line.Should().Be(2);
        thrown.InnerException.Should().BeOfType<SilverModManager.Games.Bethesda.Loot.Database.ConditionParseException>();
    }
}
