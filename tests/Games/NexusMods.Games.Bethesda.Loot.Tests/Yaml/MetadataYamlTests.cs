using NexusMods.Games.Bethesda.Loot.Metadata;
using NexusMods.Games.Bethesda.Loot.Metadata.Yaml;
using static NexusMods.Games.Bethesda.Loot.Tests.Metadata.TestNames;

namespace NexusMods.Games.Bethesda.Loot.Tests.Yaml;

// libloot's try_from_yaml tests for each metadata type. The tests for an invalid condition or
// constraint string are not here: conditions are kept as written until the condition parser exists.
public class MetadataYamlTests
{
    private static Func<object> Reading<T>(Func<YamlDotNet.RepresentationModel.YamlNode, T> read, string yaml)
        where T : notnull => () => read(TestYaml.Parse(yaml));

    public class FileTests
    {
        [Fact]
        public void ShouldOnlySetNameIfDecodingFromScalar()
        {
            var file = MetadataYaml.ReadFile(TestYaml.Parse("name1"));

            file.Name.Value.Should().Be("name1");
            file.DisplayName.Should().BeNull();
            file.Condition.Should().BeNull();
            file.Constraint.Should().BeNull();
            file.Detail.Should().BeEmpty();
        }

        [Theory]
        [InlineData("[0, 1, 2]", "\"file\" object must be a map or string")]
        [InlineData("{display: display1}", "\"name\" key in \"file\" map is missing")]
        [InlineData("{name: 123}", "\"name\" key in \"file\" map must be a string")]
        public void ShouldErrorOnInvalidInput(string yaml, string reason)
        {
            Reading(MetadataYaml.ReadFile, yaml).Should().Throw<MetadataParseException>()
                .Which.Message.Should().EndWith(reason);
        }

        [Fact]
        public void ShouldSetAllGivenFields()
        {
            var file = MetadataYaml.ReadFile(TestYaml.Parse(
                "{name: name1, display: display1, condition: 'file(\"Foo.esp\")', constraint: 'file(\"Bar.esp\")', detail: 'details'}"));

            file.Name.Value.Should().Be("name1");
            file.DisplayName.Should().Be("display1");
            file.Condition.Should().Be("file(\"Foo.esp\")");
            file.Constraint.Should().Be("file(\"Bar.esp\")");
            file.Detail.Should().Equal(new MessageContent("details"));
        }

        [Fact]
        public void ShouldLeaveOptionalFieldsEmptyIfNotPresent()
        {
            var file = MetadataYaml.ReadFile(TestYaml.Parse("{name: name1}"));

            file.DisplayName.Should().BeNull();
            file.Condition.Should().BeNull();
            file.Constraint.Should().BeNull();
            file.Detail.Should().BeEmpty();
        }

        [Fact]
        public void ShouldReadAllListedDetailMessageContents()
        {
            var file = MetadataYaml.ReadFile(TestYaml.Parse(
                "{name: name1, detail: [{text: english, lang: en}, {text: french, lang: fr}]}"));

            file.Detail.Should().Equal(new MessageContent("english"), new MessageContent("french") { Language = "fr" });
        }

        [Fact]
        public void ShouldNotErrorIfOneDetailIsGivenAndItIsNotEnglish()
        {
            var file = MetadataYaml.ReadFile(TestYaml.Parse("name: name1\ndetail:\n  - lang: fr\n    text: content1"));

            file.Detail.Should().Equal(new MessageContent("content1") { Language = "fr" });
        }

        [Fact]
        public void ShouldErrorIfMultipleDetailsAreGivenAndNoneAreEnglish()
        {
            Reading(
                MetadataYaml.ReadFile,
                "name: name1\ndetail:\n  - lang: de\n    text: content1\n  - lang: fr\n    text: content2")
                .Should().Throw<MetadataParseException>()
                .Which.Message.Should().EndWith("multilingual messages must contain a content string that uses the en language code");
        }
    }

    public class GroupTests
    {
        [Theory]
        [InlineData("[0, 1, 2]")]
        [InlineData("{description: text}")]
        [InlineData("{name: group1, after: other_group}")]
        [InlineData("{name: group1, after: [0, 1]}")]
        public void ShouldErrorOnInvalidInput(string yaml)
        {
            Reading(MetadataYaml.ReadGroup, yaml).Should().Throw<MetadataParseException>();
        }

        [Fact]
        public void ShouldSetAllGivenFields()
        {
            var group = MetadataYaml.ReadGroup(TestYaml.Parse("{name: group1, description: text, after: [ other_group ]}"));

            group.Name.Should().Be("group1");
            group.Description.Should().Be("text");
            group.AfterGroups.Should().Equal("other_group");
        }

        [Fact]
        public void ShouldLeaveOptionalFieldsEmptyIfNotPresent()
        {
            var group = MetadataYaml.ReadGroup(TestYaml.Parse("{name: group1}"));

            group.Description.Should().BeNull();
            group.AfterGroups.Should().BeEmpty();
        }

        [Fact]
        public void ShouldReportTheElementThatIsNotAString()
        {
            Reading(MetadataYaml.ReadGroup, "{name: group1, after: [other, 5]}").Should().Throw<MetadataParseException>()
                .Which.Message.Should().EndWith("\"after\" key in \"group\" map must be a string");
        }
    }

    public class LocationTests
    {
        [Fact]
        public void ShouldOnlySetUrlIfDecodingFromScalar()
        {
            var location = MetadataYaml.ReadLocation(TestYaml.Parse("https://www.example.com"));

            location.Url.Should().Be("https://www.example.com");
            location.Name.Should().BeNull();
        }

        [Theory]
        [InlineData("[0, 1, 2]")]
        [InlineData("{name: example}")]
        [InlineData("{link: [https://www.example.com], name: example}")]
        [InlineData("{link: https://www.example.com, name: [example]}")]
        [InlineData("{link: https://www.example.com}")]
        public void ShouldErrorOnInvalidInput(string yaml)
        {
            Reading(MetadataYaml.ReadLocation, yaml).Should().Throw<MetadataParseException>();
        }

        [Fact]
        public void ShouldSetAllFields()
        {
            var location = MetadataYaml.ReadLocation(TestYaml.Parse("{link: https://www.example.com, name: example}"));

            location.Url.Should().Be("https://www.example.com");
            location.Name.Should().Be("example");
        }
    }

    public class TagTests
    {
        [Fact]
        public void ShouldOnlySetNameAndSuggestionIfDecodingFromScalar()
        {
            var tag = MetadataYaml.ReadTag(TestYaml.Parse("Relev"));

            tag.Name.Should().Be("Relev");
            tag.IsAddition.Should().BeTrue();
            tag.Condition.Should().BeNull();
        }

        [Fact]
        public void ShouldReadALeadingHyphenAsARemoval()
        {
            var tag = MetadataYaml.ReadTag(TestYaml.Parse("-Relev"));

            tag.Name.Should().Be("Relev");
            tag.IsAddition.Should().BeFalse();
        }

        [Theory]
        [InlineData("[0, 1, 2]")]
        [InlineData("{condition: 'file(\"Foo.esp\")'}")]
        public void ShouldErrorOnInvalidInput(string yaml)
        {
            Reading(MetadataYaml.ReadTag, yaml).Should().Throw<MetadataParseException>();
        }

        [Fact]
        public void ShouldSetAllFields()
        {
            var tag = MetadataYaml.ReadTag(TestYaml.Parse("{name: -Relev, condition: 'file(\"Foo.esp\")'}"));

            tag.Name.Should().Be("Relev");
            tag.IsAddition.Should().BeFalse();
            tag.Condition.Should().Be("file(\"Foo.esp\")");
        }

        [Fact]
        public void ShouldLeaveOptionalFieldsEmptyIfNotPresent()
        {
            var tag = MetadataYaml.ReadTag(TestYaml.Parse("{name: Relev}"));

            tag.IsAddition.Should().BeTrue();
            tag.Condition.Should().BeNull();
        }

        [Fact]
        public void ShouldRejectANumericScalarAsAName()
        {
            Reading(MetadataYaml.ReadTag, "42").Should().Throw<MetadataParseException>()
                .Which.Message.Should().EndWith("\"tag\" object must be a map or string");
        }
    }

    public class PluginCleaningDataTests
    {
        [Theory]
        [InlineData("0x12345678")]
        [InlineData("[0, 1, 2]")]
        [InlineData("{util: cleaner}")]
        [InlineData("{crc: 0x12345678}")]
        [InlineData("{crc: 0x12345678, util: cleaner, itm: true}")]
        [InlineData("{crc: 0x12345678, util: cleaner, udr: true}")]
        [InlineData("{crc: 0x12345678, util: cleaner, nav: true}")]
        [InlineData("{crc: 0x12345678, util: cleaner, itm: -1}")]
        [InlineData("{crc: 0x12345678, util: cleaner, udr: -2}")]
        [InlineData("{crc: 0x12345678, util: cleaner, nav: -3}")]
        public void ShouldErrorOnInvalidInput(string yaml)
        {
            Reading(MetadataYaml.ReadPluginCleaningData, yaml).Should().Throw<MetadataParseException>();
        }

        [Fact]
        public void ShouldSetAllGivenFields()
        {
            var data = MetadataYaml.ReadPluginCleaningData(TestYaml.Parse(
                "{crc: 0x12345678, util: cleaner, detail: info, itm: 2, udr: 10, nav: 30, condition: file(\"example.esp\")}"));

            data.Crc.Should().Be(0x12345678u);
            data.CleaningUtility.Should().Be("cleaner");
            data.Detail.Should().Equal(new MessageContent("info"));
            data.ItmCount.Should().Be(2u);
            data.DeletedReferenceCount.Should().Be(10u);
            data.DeletedNavmeshCount.Should().Be(30u);
            data.Condition.Should().Be("file(\"example.esp\")");
        }

        [Fact]
        public void ShouldLeaveOptionalFieldsAtDefaultsIfNotPresent()
        {
            var data = MetadataYaml.ReadPluginCleaningData(TestYaml.Parse("{crc: 0x12345678, util: cleaner}"));

            data.Detail.Should().BeEmpty();
            data.ItmCount.Should().Be(0u);
            data.DeletedReferenceCount.Should().Be(0u);
            data.DeletedNavmeshCount.Should().Be(0u);
            data.Condition.Should().BeNull();
        }

        [Fact]
        public void ShouldReadAllListedDetailMessageContents()
        {
            var data = MetadataYaml.ReadPluginCleaningData(TestYaml.Parse(
                "{crc: 0x12345678, util: cleaner, detail: [{text: english, lang: en}, {text: french, lang: fr}]}"));

            data.Detail.Should().Equal(new MessageContent("english"), new MessageContent("french") { Language = "fr" });
        }

        [Fact]
        public void ShouldNotErrorIfOneDetailIsGivenAndItIsNotEnglish()
        {
            var data = MetadataYaml.ReadPluginCleaningData(TestYaml.Parse(
                "crc: 0x12345678\nutil: cleaner\ndetail:\n  - lang: fr\n    text: content1"));

            data.Detail.Should().Equal(new MessageContent("content1") { Language = "fr" });
        }

        [Fact]
        public void ShouldErrorIfMultipleDetailsAreGivenAndNoneAreEnglish()
        {
            Reading(
                MetadataYaml.ReadPluginCleaningData,
                "crc: 0x12345678\nutil: cleaner\ndetail:\n  - lang: de\n    text: content1\n  - lang: fr\n    text: content2")
                .Should().Throw<MetadataParseException>();
        }

        [Theory]
        [InlineData("crc: 0xDEADBEEF")]
        [InlineData("crc: 0xdeadbeef")]
        [InlineData("crc: 3735928559")]
        [InlineData("crc: +3735928559")]
        [InlineData("crc: 0o33653337357")]
        public void ShouldReadHexDecimalAndOctalIntegersForTheCrc(string crc)
        {
            var data = MetadataYaml.ReadPluginCleaningData(TestYaml.Parse($"{{{crc}, util: cleaner}}"));

            data.Crc.Should().Be(0xDEADBEEF);
        }

        [Fact]
        public void ShouldReadHexIntegersForTheCounts()
        {
            var data = MetadataYaml.ReadPluginCleaningData(TestYaml.Parse("{crc: 1, util: cleaner, itm: 0x10, udr: 0o17, nav: 7}"));

            data.ItmCount.Should().Be(16u);
            data.DeletedReferenceCount.Should().Be(15u);
            data.DeletedNavmeshCount.Should().Be(7u);
        }

        [Fact]
        public void ShouldReadTheLargestU32()
        {
            var data = MetadataYaml.ReadPluginCleaningData(TestYaml.Parse("{crc: 4294967295, util: cleaner}"));

            data.Crc.Should().Be(uint.MaxValue);
        }

        [Fact]
        public void ShouldTreatAQuotedIntegerAsATypeError()
        {
            Reading(MetadataYaml.ReadPluginCleaningData, "{crc: '0xDEADBEEF', util: cleaner}")
                .Should().Throw<MetadataParseException>()
                .Which.Message.Should().EndWith("\"crc\" key in \"plugin cleaning data\" map must be a number");
        }

        [Fact]
        public void ShouldReportAnIntegerAboveU32Range()
        {
            Reading(MetadataYaml.ReadPluginCleaningData, "{crc: 4294967296, util: cleaner}")
                .Should().Throw<MetadataParseException>()
                .Which.Message.Should().EndWith("4294967296 is not valid as a 32-bit unsigned integer");
        }

        [Fact]
        public void ShouldReportANegativeCount()
        {
            Reading(MetadataYaml.ReadPluginCleaningData, "{crc: 1, util: cleaner, itm: -1}")
                .Should().Throw<MetadataParseException>()
                .Which.Message.Should().EndWith("-1 is not valid as a 32-bit unsigned integer");
        }

        [Theory]
        [InlineData("1.5")]
        [InlineData("0xGG")]
        [InlineData("99999999999999999999")]
        [InlineData("~")]
        public void ShouldTreatAPlainNonIntegerAsATypeError(string crc)
        {
            Reading(MetadataYaml.ReadPluginCleaningData, $"{{crc: {crc}, util: cleaner}}")
                .Should().Throw<MetadataParseException>()
                .Which.Message.Should().EndWith("\"crc\" key in \"plugin cleaning data\" map must be a number");
        }

        [Fact]
        public void ShouldLocateAnOutOfRangeNumberAtItsValue()
        {
            var error = Reading(MetadataYaml.ReadPluginCleaningData, "crc: 4294967296\nutil: cleaner")
                .Should().Throw<MetadataParseException>().Which;

            error.Line.Should().Be(1);
            error.Column.Should().Be(6);
            error.Message.Should().StartWith("encountered a YAML parsing error at line 1 column 6: ");
        }
    }

    public class MessageContentTests
    {
        [Theory]
        [InlineData("content")]
        [InlineData("[0, 1, 2]")]
        public void ShouldErrorOnInvalidInput(string yaml)
        {
            Reading(MetadataYaml.ReadMessageContent, yaml).Should().Throw<MetadataParseException>();
        }

        [Fact]
        public void ShouldSetAllGivenFields()
        {
            var content = MetadataYaml.ReadMessageContent(TestYaml.Parse("{text: content, lang: fr}"));

            content.Text.Should().Be("content");
            content.Language.Should().Be("fr");
        }

        [Fact]
        public void ShouldRequireTheLanguage()
        {
            // The wording names the "message" map, as libloot's does.
            Reading(MetadataYaml.ReadMessageContent, "{text: content}").Should().Throw<MetadataParseException>()
                .Which.Message.Should().EndWith("\"lang\" key in \"message\" map is missing");
        }
    }

    public class MessageTests
    {
        [Theory]
        [InlineData("content")]
        [InlineData("[0, 1, 2]")]
        [InlineData("{type: say}")]
        [InlineData("{type: say, content: 5}")]
        [InlineData("{type: say, content: 'content1', subs: [sub1]}")]
        [InlineData("{type: say, content: '{0} {1}', subs: [sub1]}")]
        public void ShouldErrorOnInvalidInput(string yaml)
        {
            Reading(MetadataYaml.ReadMessage, yaml).Should().Throw<MetadataParseException>();
        }

        [Fact]
        public void ShouldSetAllGivenFields()
        {
            var message = MetadataYaml.ReadMessage(TestYaml.Parse("{type: say, content: text, condition: 'file(\"Foo.esp\")'}"));

            message.Type.Should().Be(MessageType.Say);
            message.Content.Should().Equal(new MessageContent("text"));
            message.Condition.Should().Be("file(\"Foo.esp\")");
        }

        [Fact]
        public void ShouldLeaveOptionalFieldsEmptyIfNotPresent()
        {
            var message = MetadataYaml.ReadMessage(TestYaml.Parse("{type: say, content: text}"));

            message.Condition.Should().BeNull();
        }

        [Theory]
        [InlineData("say", MessageType.Say)]
        [InlineData("warn", MessageType.Warn)]
        [InlineData("error", MessageType.Error)]
        [InlineData("info", MessageType.Say)]
        public void ShouldReadTheMessageType(string type, MessageType expected)
        {
            MetadataYaml.ReadMessage(TestYaml.Parse($"{{type: {type}, content: text}}")).Type.Should().Be(expected);
        }

        [Fact]
        public void ShouldReadAllListedMessageContents()
        {
            var message = MetadataYaml.ReadMessage(TestYaml.Parse(
                "{type: say, content: [{text: english, lang: en}, {text: french, lang: fr}]}"));

            message.Content.Should().Equal(new MessageContent("english"), new MessageContent("french") { Language = "fr" });
        }

        [Fact]
        public void ShouldNotErrorIfOneContentObjectIsGivenAndItIsNotEnglish()
        {
            var message = MetadataYaml.ReadMessage(TestYaml.Parse("type: say\ncontent:\n  - lang: fr\n    text: content1"));

            message.Content.Should().Equal(new MessageContent("content1") { Language = "fr" });
        }

        [Fact]
        public void ShouldErrorIfMultipleContentsAreGivenAndNoneAreEnglish()
        {
            Reading(
                MetadataYaml.ReadMessage,
                "type: say\ncontent:\n  - lang: de\n    text: content1\n  - lang: fr\n    text: content2")
                .Should().Throw<MetadataParseException>();
        }

        [Fact]
        public void ShouldApplySubstitutionsWhenThereIsOnlyOneContentString()
        {
            var message = MetadataYaml.ReadMessage(TestYaml.Parse("type: say\ncontent: con{0}tent1\nsubs:\n  - sub1"));

            message.Content[0].Text.Should().Be("consub1tent1");
        }

        [Fact]
        public void ShouldApplySubstitutionsToAllContentStrings()
        {
            var message = MetadataYaml.ReadMessage(TestYaml.Parse(
                "type: say\ncontent:\n  - lang: en\n    text: content1 {0}\n  - lang: fr\n    text: content2 {0}\nsubs:\n  - sub"));

            message.Content[0].Text.Should().Be("content1 sub");
            message.Content[1].Text.Should().Be("content2 sub");
        }

        [Fact]
        public void ShouldIgnoreSubstitutionSyntaxIfNoSubstitutionsExist()
        {
            var message = MetadataYaml.ReadMessage(TestYaml.Parse("{type: say, content: 'content {0}'}"));

            message.Content[0].Text.Should().Be("content {0}");
        }

        [Fact]
        public void ShouldReportAMissingSubstitutionAtTheMessage()
        {
            Reading(MetadataYaml.ReadMessage, "{type: say, content: '{0} {1}', subs: [sub1]}")
                .Should().Throw<MetadataParseException>()
                .Which.Message.Should().EndWith("failed to substitute a value into message, no substitution was given for the placeholder \"{1}\"");
        }
    }

    public class PluginMetadataTests
    {
        [Theory]
        [InlineData("name1")]
        [InlineData("[0, 1, 2]")]
        [InlineData("{name: 'Blank.esp', after: Blank.esm}")]
        public void ShouldErrorOnInvalidInput(string yaml)
        {
            Reading(MetadataYaml.ReadPluginMetadata, yaml).Should().Throw<MetadataParseException>();
        }

        [Fact]
        public void ShouldStoreAllGivenData()
        {
            var plugin = MetadataYaml.ReadPluginMetadata(TestYaml.Parse("""
                name: 'Blank.esp'
                group: late
                after:
                  - 'Blank.esm'
                req:
                  - 'Blank - Different.esm'
                inc:
                  - 'Blank - Different.esp'
                msg:
                  - type: say
                    content: 'content'
                tag:
                  - Relev
                dirty:
                  - crc: 0x5
                    util: 'utility'
                clean:
                  - crc: 0x6
                    util: 'utility'
                url:
                  - 'https://www.example.com'
                """));

            plugin.Name.Should().Be(BlankEsp);
            plugin.Group.Should().Be("late");
            plugin.LoadAfter.Should().Equal(new File(BlankEsm));
            plugin.Requirements.Should().Equal(new File(BlankDifferentEsm));
            plugin.Incompatibilities.Should().Equal(new File(BlankDifferentEsp));
            plugin.Messages.Should().Equal(new Message(MessageType.Say, "content"));
            plugin.Tags.Should().Equal(new Tag("Relev"));
            plugin.DirtyInfo.Should().Equal(new PluginCleaningData(0x5, "utility"));
            plugin.CleanInfo.Should().Equal(new PluginCleaningData(0x6, "utility"));
            plugin.Locations.Should().Equal(new Location("https://www.example.com"));
        }

        [Fact]
        public void ShouldNotErrorIfRegexMetadataContainsDirtyOrCleanInfo()
        {
            var plugin = MetadataYaml.ReadPluginMetadata(TestYaml.Parse("""
                name: 'Blank\.esp'
                dirty:
                  - crc: 0x5
                    util: 'utility'
                clean:
                  - crc: 0x6
                    util: 'utility'
                """));

            plugin.Name.Should().Be("Blank\\.esp");
            plugin.DirtyInfo.Should().Equal(new PluginCleaningData(0x5, "utility"));
            plugin.CleanInfo.Should().Equal(new PluginCleaningData(0x6, "utility"));
        }

        [Fact]
        public void ShouldErrorIfRegexNameIsInvalid()
        {
            var error = Reading(MetadataYaml.ReadPluginMetadata, "{name: 'RagnvaldBook(Farengar(+Ragnvald)?)?\\.esp'}")
                .Should().Throw<MetadataParseException>().Which;

            error.Message.Should().EndWith("invalid regex in \"name\" key with value \"RagnvaldBook(Farengar(+Ragnvald)?)?\\.esp\"");
            error.InnerException.Should().BeOfType<InvalidPluginNameException>();
        }

        [Fact]
        public void ShouldIgnoreKeysItDoesNotKnow()
        {
            var plugin = MetadataYaml.ReadPluginMetadata(TestYaml.Parse("{name: Blank.esm, priority: -100}"));

            plugin.HasNameOnly.Should().BeTrue();
        }
    }
}
