using SilverModManager.Games.Bethesda.Loot.Metadata;
using NexusMods.Paths;
using static SilverModManager.Games.Bethesda.Loot.Tests.Metadata.TestNames;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Metadata;

// libloot's metadata_document.rs load and find_plugin tests. The write and emit tests are not ported.
public class MetadataDocumentTests
{
    private const string MetadataListYaml = """
        bash_tags:
          - 'C.Climate'
          - 'Relev'

        groups:
          - name: group1
            after:
              - group2
          - name: group2
            after:
              - default

        globals:
          - type: say
            content: 'A global message.'

        plugins:
          - name: 'Blank.esm'
            priority: -100
            msg:
              - type: warn
                content: 'This is a warning.'
              - type: say
                content: 'This message should be removed when evaluating conditions.'
                condition: 'active("Blank - Different.esm")'

          - name: 'Blank.+\.esp'
            after:
              - 'Blank.esm'

          - name: 'Blank.+(Different)?.*\.esp'
            inc:
              - 'Blank.esp'

          - name: 'Blank.esp'
            group: group2
            dirty:
              - crc: 0xDEADBEEF
                util: utility
        """;

    public class LoadTests
    {
        [Fact]
        public void ShouldDeserialiseAMasterlist()
        {
            var document = MetadataDocument.Load(MetadataListYaml);

            document.Plugins.Keys.Select(k => k.Value).Should().BeEquivalentTo(BlankEsm, BlankEsp);
            document.RegexPlugins.Select(p => p.Name).Should().Equal("Blank.+\\.esp", "Blank.+(Different)?.*\\.esp");
            document.BashTags.Should().Equal("C.Climate", "Relev");
            document.Globals.Should().Equal(new Message(MessageType.Say, "A global message."));

            document.Groups.Select(g => g.Name).Should().Equal("default", "group1", "group2");
            document.Groups[0].AfterGroups.Should().BeEmpty();
            document.Groups[1].AfterGroups.Should().Equal("group2");
            document.Groups[2].AfterGroups.Should().Equal("default");

            document.Plugins[new Filename(BlankEsp)].Group.Should().Be("group2");
            document.Plugins[new Filename(BlankEsp)].DirtyInfo.Should().Equal(
                new PluginCleaningData(0xDEADBEEF, "utility"));
        }

        [Fact]
        public void ShouldKeepConditionStringsAsWritten()
        {
            var document = MetadataDocument.Load(MetadataListYaml);

            document.Plugins[new Filename(BlankEsm)].Messages[1].Condition.Should().Be("active(\"Blank - Different.esm\")");
        }

        [Fact]
        public void ShouldResolveAliases()
        {
            var document = MetadataDocument.Load("""
                prelude:
                  - &anchor
                    type: say
                    content: test message

                globals:
                  - *anchor
                """);

            document.Globals.Should().Equal(new Message(MessageType.Say, "test message"));
        }

        [Fact]
        public void ShouldResolveMergeKeys()
        {
            var document = MetadataDocument.Load("""
                prelude:
                  - &anchor
                    type: say
                    content: test message

                globals:
                  - <<: *anchor
                    condition: file("test.esp")
                """);

            document.Globals.Should().Equal(new Message(MessageType.Say, "test message") { Condition = "file(\"test.esp\")" });
        }

        [Fact]
        public void EmptyShouldHoldOnlyTheDefaultGroup()
        {
            var document = MetadataDocument.Empty;

            document.Groups.Should().ContainSingle().Which.Name.Should().Be("default");
            document.Plugins.Should().BeEmpty();
            document.RegexPlugins.Should().BeEmpty();
            document.Globals.Should().BeEmpty();
            document.BashTags.Should().BeEmpty();
            document.Prelude.Should().BeNull();
        }

        [Fact]
        public void ShouldKeepThePreludeNode()
        {
            MetadataDocument.Load("prelude:\n  - a\nglobals: []").Prelude.Should().NotBeNull();
            MetadataDocument.Load("globals: []").Prelude.Should().BeNull();
        }

        [Fact]
        public void ShouldErrorIfAPluginHasTwoExactEntries()
        {
            Action act = () => MetadataDocument.Load("""
                plugins:
                  - name: 'Blank.esm'
                    msg:
                      - type: warn
                        content: 'This is a warning.'

                  - name: 'blank.esm'
                    msg:
                      - type: error
                        content: 'This plugin entry will cause a failure, as it is not the first exact entry.'
                """);

            act.Should().Throw<MetadataParseException>()
                .Which.Message.Should().EndWith("more than one entry exists for plugin metadata \"Blank.esm\"");
        }

        [Fact]
        public void ShouldAcceptTwoRegexEntriesWithTheSameName()
        {
            var document = MetadataDocument.Load("plugins:\n  - name: 'a.*\\.esp'\n  - name: 'a.*\\.esp'\n");

            document.RegexPlugins.Should().HaveCount(2);
        }

        [Fact]
        public void ShouldErrorIfTwoGroupsShareAName()
        {
            Action act = () => MetadataDocument.Load("groups:\n  - name: g\n  - name: g\n");

            act.Should().Throw<MetadataParseException>()
                .Which.Message.Should().EndWith("more than one entry exists for group \"g\"");
        }

        [Fact]
        public void ShouldCompareGroupNamesCaseSensitively()
        {
            var document = MetadataDocument.Load("groups:\n  - name: g\n  - name: G\n");

            document.Groups.Select(g => g.Name).Should().Equal("default", "g", "G");
        }

        [Fact]
        public void ShouldInsertTheDefaultGroupFirstIfMissing()
        {
            MetadataDocument.Load("groups:\n  - name: other\n").Groups.Select(g => g.Name)
                .Should().Equal("default", "other");
            MetadataDocument.Load("globals: []").Groups.Select(g => g.Name).Should().Equal("default");
        }

        [Fact]
        public void ShouldKeepAnExplicitDefaultGroupWhereItIs()
        {
            var document = MetadataDocument.Load("groups:\n  - name: other\n  - name: default\n    description: mine\n");

            document.Groups.Select(g => g.Name).Should().Equal("other", "default");
            document.Groups[1].Description.Should().Be("mine");
        }

        [Fact]
        public void ShouldErrorIfAnInvalidMetadataFileIsGiven()
        {
            var yaml = "  - 'C.Climate'\n  - 'Relev'\n\nglobals:\n  - type: say\n    content: 'A global message.'\n";

            Action act = () => MetadataDocument.Load(yaml);

            act.Should().Throw<MetadataParseException>();
        }

        [Fact]
        public void ShouldErrorOnAnEmptyString()
        {
            Action act = () => MetadataDocument.Load("");

            act.Should().Throw<MetadataLoadException>()
                .Where(e => e.Failure == MetadataLoadFailure.NoDocuments)
                .WithMessage("no YAML document found");
        }

        [Theory]
        [InlineData("   \n")]
        [InlineData("# only a comment\n")]
        public void ShouldTreatAnInputWithNoDocumentAsNoDocuments(string yaml)
        {
            Action act = () => MetadataDocument.Load(yaml);

            act.Should().Throw<MetadataLoadException>().Where(e => e.Failure == MetadataLoadFailure.NoDocuments);
        }

        [Fact]
        public void ShouldErrorOnMoreThanOneDocument()
        {
            Action act = () => MetadataDocument.Load("globals: []\n---\nglobals: []\n");

            act.Should().Throw<MetadataLoadException>()
                .Where(e => e.Failure == MetadataLoadFailure.MoreThanOneDocument)
                .WithMessage("expected 1 YAML document, found 2");
        }

        [Fact]
        public void ShouldErrorIfTheDocumentIsNotAMapping()
        {
            Action act = () => MetadataDocument.Load("- a\n- b\n");

            act.Should().Throw<MetadataParseException>()
                .Which.Message.Should().EndWith("\"metadata document\" object must be a map");
        }

        [Fact]
        public void ShouldErrorIfPluginsIsNotAnArray()
        {
            Action act = () => MetadataDocument.Load("plugins: a.esp\n");

            act.Should().Throw<MetadataParseException>()
                .Which.Message.Should().EndWith("\"plugins\" key in \"metadata document\" map must be an array");
        }

        [Fact]
        public void ShouldErrorIfABashTagIsNotAString()
        {
            Action act = () => MetadataDocument.Load("bash_tags:\n  - Relev\n  - 42\n");

            act.Should().Throw<MetadataParseException>()
                .Which.Message.Should().EndWith("\"bash tags\" object must be a string");
        }

        [Fact]
        public void ShouldReportAnUnknownAnchorAtTheAlias()
        {
            Action act = () => MetadataDocument.Load("common:\n  - *invalid\nx: 1\n");

            var error = act.Should().Throw<MetadataParseException>().Which;
            error.Line.Should().Be(2);
            error.Column.Should().Be(5);
        }

        [Fact]
        public void ShouldReportAScannerErrorAtItsPosition()
        {
            Action act = () => MetadataDocument.Load("globals:\n  - type\n    content: x\n");

            var error = act.Should().Throw<MetadataParseException>().Which;
            error.Line.Should().Be(3);
            error.IsWithinPrelude.Should().BeFalse();
        }

        [Fact]
        public void ShouldNotCarryOverAnythingFromAPreviousLoad()
        {
            MetadataDocument.Load(MetadataListYaml);

            MetadataDocument.Load("globals: []").Plugins.Should().BeEmpty();
        }

        [Fact]
        public void ShouldParseAHandWrittenMasterlistThatUsesAnchorsAliasesAndMergeKeys()
        {
            var document = MetadataDocument.Load("""
                bash_tags:
                  - Delev
                  - Relev

                common:
                  - &patchMessage
                    type: warn
                    content:
                      - text: 'Needs the patch.'
                        lang: en
                      - text: 'Benotigt den Patch.'
                        lang: de
                  - &dirtyUtil 'SSEEdit v4.0.3'
                  - &masterFile 'Skyrim.esm'
                  - &lateGroup late

                groups:
                  - name: early
                    description: Loads first.
                  - name: &late late
                    after:
                      - early
                  - name: last
                    after: [*late]

                globals:
                  - type: say
                    content: Global note.
                  - <<: *patchMessage
                    type: error

                plugins:
                  - name: 'Unofficial Patch.esp'
                    group: *lateGroup
                    after:
                      - *masterFile
                      - name: 'Update.esm'
                        display: '[Update](https://example.com)'
                        detail: Needs this.
                    req:
                      - *masterFile
                    msg:
                      - *patchMessage
                      - <<: *patchMessage
                        content: Overridden text.
                    tag:
                      - Delev
                      - -Relev
                    dirty:
                      - crc: 0xDEADBEEF
                        util: *dirtyUtil
                        itm: 4
                        udr: 0x10
                        nav: 2
                    url:
                      - https://example.com/patch
                      - link: https://example.com/other
                        name: Other site

                  - name: 'Patch.*\.esp'
                    group: last
                    after:
                      - 'Unofficial Patch.esp'

                  - name: 'Dummy.esp'
                    inc:
                      - Skyrim.esm
                """);

            document.BashTags.Should().Equal("Delev", "Relev");
            document.Groups.Select(g => g.Name).Should().Equal("default", "early", "late", "last");
            document.Groups[3].AfterGroups.Should().Equal("late");
            document.Globals.Should().HaveCount(2);
            document.Globals[1].Type.Should().Be(MessageType.Error);
            document.Globals[1].Content.Should().HaveCount(2);

            var plugin = document.Plugins[new Filename("unofficial patch.esp")];
            plugin.Group.Should().Be("late");
            plugin.LoadAfter.Select(f => f.Name.Value).Should().Equal("Skyrim.esm", "Update.esm");
            plugin.LoadAfter[1].DisplayName.Should().Be("[Update](https://example.com)");
            plugin.Requirements.Should().Equal(new File("Skyrim.esm"));
            plugin.Messages.Should().HaveCount(2);
            plugin.Messages[0].Type.Should().Be(MessageType.Warn);
            plugin.Messages[1].Content.Should().Equal(new MessageContent("Overridden text."));
            plugin.Tags.Should().Equal(new Tag("Delev"), new Tag("Relev", TagSuggestion.Removal));
            plugin.DirtyInfo.Should().ContainSingle().Which.Should().Be(
                new PluginCleaningData(0xDEADBEEF, "SSEEdit v4.0.3") { ItmCount = 4, DeletedReferenceCount = 16, DeletedNavmeshCount = 2 });
            plugin.Locations.Should().Equal(
                new Location("https://example.com/patch"),
                new Location("https://example.com/other") { Name = "Other site" });

            document.RegexPlugins.Should().ContainSingle().Which.Name.Should().Be("Patch.*\\.esp");
            document.Plugins.Keys.Select(k => k.Value).Should().BeEquivalentTo("Unofficial Patch.esp", "Dummy.esp");
        }
    }

    public class LoadFileTests : IDisposable
    {
        private readonly AbsolutePath _directory;

        public LoadFileTests()
        {
            _directory = FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(Path.GetTempPath(), "loot-" + Guid.NewGuid().ToString("N")));
            _directory.CreateDirectory();
        }

        public void Dispose() => _directory.DeleteDirectory(true);

        [Fact]
        public void ShouldDeserialiseAMasterlistFile()
        {
            var path = _directory / "masterlist.yaml";
            System.IO.File.WriteAllText(path.ToString(), MetadataListYaml);

            var document = MetadataDocument.LoadFile(path);

            document.Plugins.Should().HaveCount(2);
            document.RegexPlugins.Should().HaveCount(2);
        }

        [Fact]
        public void ShouldErrorIfThePathDoesNotExist()
        {
            Action act = () => MetadataDocument.LoadFile(_directory / "missing.yaml");

            act.Should().Throw<MetadataLoadException>().Where(e => e.Failure == MetadataLoadFailure.PathNotFound);
        }
    }

    public class FindPluginTests
    {
        [Fact]
        public void ShouldReturnNullIfThePluginHasNoMetadata()
        {
            MetadataDocument.Load("globals: []").FindPlugin(BlankEsp).Should().BeNull();
        }

        [Fact]
        public void ShouldReturnTheMetadataIfOneExists()
        {
            var document = MetadataDocument.Load(MetadataListYaml);

            var name = "Blank - Different.esp";
            var plugin = document.FindPlugin(name)!;

            plugin.Name.Should().Be(name);
            plugin.LoadAfter.Should().Equal(new File(BlankEsm));
            plugin.Incompatibilities.Should().Equal(new File(BlankEsp));
        }

        [Fact]
        public void ShouldMergeTheExactEntryWithEveryMatchingRegexEntryInFileOrder()
        {
            var document = MetadataDocument.Load("""
                plugins:
                  - name: 'A.*\.esp'
                    after: [first]
                    group: regexGroup
                  - name: 'A.esp'
                    after: [exact]
                  - name: 'A\.esp'
                    after: [second, first]
                    group: otherGroup
                """);

            var plugin = document.FindPlugin("A.esp")!;

            plugin.Name.Should().Be("A.esp");
            // The exact entry is the base: it contributes first, then each regex entry in file order.
            plugin.LoadAfter.Select(f => f.Name.Value).Should().Equal("exact", "first", "second");
            plugin.Group.Should().Be("regexGroup");
        }

        [Fact]
        public void ShouldMatchTheExactNameCaseInsensitively()
        {
            var document = MetadataDocument.Load(MetadataListYaml);

            document.FindPlugin("BLANK.ESP")!.Group.Should().Be("group2");
        }

        [Fact]
        public void ShouldReturnRegexOnlyMetadataNamedAfterTheQuery()
        {
            var document = MetadataDocument.Load(MetadataListYaml);

            var plugin = document.FindPlugin("Blank - Other.esp")!;

            plugin.Name.Should().Be("Blank - Other.esp");
            plugin.LoadAfter.Should().Equal(new File(BlankEsm));
        }

        [Fact]
        public void ShouldReturnNullIfTheMergedResultHasOnlyAName()
        {
            var document = MetadataDocument.Load("plugins:\n  - name: 'A.esp'\n  - name: 'A.*\\.esp'\n");

            document.FindPlugin("A.esp").Should().BeNull();
        }

        [Fact]
        public void ShouldNotMatchARegexEntryAgainstAnotherName()
        {
            var document = MetadataDocument.Load(MetadataListYaml);

            document.FindPlugin("Other.esp").Should().BeNull();
        }
    }

    public class LoadWithPreludeTests
    {
        private const string Prelude = "common:\n  - &ref\n    type: say\n    content: Loaded from prelude\n\n  - &otherRef\n    type: error\n    content: An error message";

        private static readonly Message[] PreludeMessages =
        [
            new(MessageType.Say, "Loaded from prelude"),
            new(MessageType.Error, "An error message"),
        ];

        [Fact]
        public void ShouldMergeDocsWithLfLineEndings()
        {
            var masterlist = "prelude:\n  - &ref\n    type: say\n    content: Loaded from same file\n\n  - &otherRef\n    type: error\n    content: Error from same file\nglobals:\n  - *ref\n  - *otherRef";

            var document = MetadataDocument.LoadWithPrelude(masterlist, Prelude);

            document.Globals.Should().Equal(PreludeMessages);
        }

        [Fact]
        public void ShouldMergeDocsWithCrlfLineEndings()
        {
            var masterlist = "prelude:\r\n  - &ref\r\n    type: say\r\n    content: Loaded from same file\r\n\r\n  - &otherRef\r\n    type: error\r\n    content: Error from same file\r\nglobals:\r\n  - *ref\r\n  - *otherRef";

            var document = MetadataDocument.LoadWithPrelude(masterlist, Prelude.Replace("\n", "\r\n"));

            document.Globals.Should().Equal(PreludeMessages);
        }

        [Fact]
        public void ShouldLoadAMasterlistWithoutAPreludeKeyUnchanged()
        {
            var document = MetadataDocument.LoadWithPrelude("globals:\n  - type: say\n    content: hi\n", Prelude);

            document.Globals.Should().Equal(new Message(MessageType.Say, "hi"));
        }

        [Fact]
        public void ShouldNotChangeParseErrorLineNumbersBeforeThePrelude()
        {
            var masterlist = "common:\n  - *invalid\nprelude:\n  - &ref\n    type: say\n    content: Loaded from same file\n\n  - &otherRef\n    type: error\n    content: Error from same file\nglobals:\n  - *ref\n  - *otherRef";

            Action act = () => MetadataDocument.LoadWithPrelude(masterlist, Prelude);

            var error = act.Should().Throw<MetadataParseException>().Which;
            error.Line.Should().Be(2);
            error.Column.Should().Be(5);
            error.IsWithinPrelude.Should().BeFalse();
            error.Message.Should().StartWith("encountered a YAML parsing error at line 2 column 5: ");
        }

        [Fact]
        public void ShouldChangeParseErrorLineNumbersWithinThePrelude()
        {
            var masterlist = "prelude:\n  - &ref\n    type: say\n    content: Loaded from same file\n\n  - &otherRef\n    type: error\n    content: Error from same file\nglobals:\n  - *ref\n  - *otherRef";
            var prelude = "common:\n  - &ref\n    type\n    content: Loaded from prelude\n\n  - &otherRef\n    type: error\n    content: An error message";

            Action act = () => MetadataDocument.LoadWithPrelude(masterlist, prelude);

            // The error is in the prelude's own line 4, shown as that line of the prelude file.
            var error = act.Should().Throw<MetadataParseException>().Which;
            error.IsWithinPrelude.Should().BeTrue();
            error.Line.Should().Be(4);
            error.Message.Should().StartWith("encountered a YAML parsing error at line 4 column ");
            error.Message.Should().Contain(" within the prelude: ");
        }

        [Fact]
        public void ShouldChangeParseErrorLineNumbersAfterThePrelude()
        {
            var masterlist = "prelude:\n  - &ref\n    type: say\n    content: Loaded from same file\n\n  - &otherRef\n    type: error\n    content: Error from same file\nglobals:\n  - *invalid\n  - *otherRef";

            Action act = () => MetadataDocument.LoadWithPrelude(masterlist, Prelude);

            // The anchor lookup fails while the document loads; the alias is on line 10 of the original.
            var error = act.Should().Throw<MetadataParseException>().Which;
            error.Line.Should().Be(10);
            error.Column.Should().Be(5);
            error.IsWithinPrelude.Should().BeFalse();
        }

        [Fact]
        public void ShouldNotChangeMergeKeyErrorLineNumbersBeforeThePrelude()
        {
            var masterlist = "common:\n  - <<: 1\nprelude:\n  - &ref\n    type: say\n    content: Loaded from same file\n\n  - &otherRef\n    type: error\n    content: Error from same file\nglobals:\n  - *ref\n  - *otherRef";

            Action act = () => MetadataDocument.LoadWithPrelude(masterlist, Prelude);

            act.Should().Throw<YamlMergeKeyException>()
                .WithMessage("invalid YAML merge key value at line 2 column 9: 1");
        }

        [Fact]
        public void ShouldChangeMergeKeyErrorLineNumbersWithinThePrelude()
        {
            var masterlist = "prelude:\n  - &ref\n    type: say\n    content: Loaded from same file\n\n  - &otherRef\n    type: error\n    content: Error from same file\nglobals:\n  - *ref\n  - *otherRef";
            var prelude = "common:\n  - &ref\n    <<: type\n    content: Loaded from prelude\n\n  - &otherRef\n    type: error\n    content: An error message";

            Action act = () => MetadataDocument.LoadWithPrelude(masterlist, prelude);

            act.Should().Throw<YamlMergeKeyException>()
                .WithMessage("invalid YAML merge key value at line 3 column 9 within the prelude: type");
        }

        [Fact]
        public void ShouldChangeMergeKeyErrorLineNumbersAfterThePrelude()
        {
            var masterlist = "prelude:\n  - &ref\n    type: say\n    content: Loaded from same file\n\n  - &otherRef\n    type: error\n    content: Error from same file\nglobals:\n  - <<: 1\n  - *otherRef";

            Action act = () => MetadataDocument.LoadWithPrelude(masterlist, Prelude);

            act.Should().Throw<YamlMergeKeyException>()
                .WithMessage("invalid YAML merge key value at line 10 column 9: 1");
        }

        [Fact]
        public void ShouldLetThePreludeDefineAnchorsTheMasterlistUses()
        {
            var masterlist = "prelude:\n  - placeholder\n\nplugins:\n  - name: a.esp\n    msg:\n      - *ref\n";
            var prelude = "- &ref\n  type: warn\n  content: from prelude\n";

            var document = MetadataDocument.LoadWithPrelude(masterlist, prelude);

            document.Plugins[new Filename("a.esp")].Messages.Should().Equal(new Message(MessageType.Warn, "from prelude"));
        }
    }
}
