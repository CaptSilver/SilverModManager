using SilverModManager.Games.Bethesda.Loot.Conditions;
using SilverModManager.Games.Bethesda.Loot.Database;
using SilverModManager.Games.Bethesda.Loot.Tests.Conditions;
using SilverModManager.Games.Bethesda.Loot.Metadata;
using SilverModManager.Games.Bethesda.Loot.Sorting;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Database;

// Ports the tests in libloot's database/mod.rs that need no plugin files, plus the counting of
// conditions the database cannot evaluate yet.
public class LootDatabaseTests
{
    private const string BlankEsm = "Blank.esm";
    private const string BlankDifferentEsm = "Blank - Different.esm";

    private const string Prelude = "- &preludeBashTag Actors.ACBS";

    private const string Masterlist = """
        prelude:
          - &preludeBashTag C.Climate
        bash_tags:
          - *preludeBashTag
        globals:
          - type: say
            content: 'A general message'
            condition: 'file("missing.esp")'
          - type: say
            content: 'An unconditional message'
        groups:
          - name: group1
          - name: group2
            after:
              - group1
        plugins:
          - name: Blank.esm
            after:
              - Oblivion.esm
            msg:
              - type: say
                content: 'A note message'
                condition: 'file("missing.esp")'
            tag:
              - Actors.ACBS
              - Actors.AIData
              - '-C.Water'
          - name: Blank - Different.esm
            after:
              - Blank - Master Dependent.esm
            msg:
              - type: warn
                content: 'A warning message'
            dirty:
              - crc: 0x7d22f9df
                util: TES4Edit
                udr: 4
          - name: Blank - Different.esp
            after:
              - Blank - Plugin Dependent.esp
            msg:
              - type: error
                content: 'An error message'
        """;

    private static LootDatabase LoadedDatabase(string? userlist = null)
    {
        var database = new LootDatabase();
        database.LoadMasterlist(Masterlist);
        if (userlist is not null) database.LoadUserlist(userlist);
        return database;
    }

    [Fact]
    public void LoadMasterlistShouldReplaceThePreviousMasterlist()
    {
        var database = LoadedDatabase();

        database.LoadMasterlist("bash_tags: [Relev]");

        database.KnownBashTags(MergeMode.WithoutUserMetadata).Should().Equal("Relev");
    }

    [Fact]
    public void LoadMasterlistWithPreludeShouldUseThePreludeInPlaceOfTheEmbeddedOne()
    {
        var database = new LootDatabase();

        database.LoadMasterlistWithPrelude(Masterlist, Prelude);

        database.KnownBashTags(MergeMode.WithoutUserMetadata).Should().Equal("Actors.ACBS");
    }

    [Fact]
    public void LoadUserlistShouldReplaceThePreviousUserlist()
    {
        var database = LoadedDatabase("bash_tags: [Relev]");

        database.LoadUserlist("bash_tags: [Delev]");

        database.KnownBashTags(MergeMode.WithUserMetadata).Should().Equal("C.Climate", "Delev");
    }

    [Fact]
    public void LoadingInvalidMetadataShouldThrowAndKeepWhatWasLoaded()
    {
        var database = LoadedDatabase();

        var load = () => database.LoadMasterlist("plugins: 5");

        load.Should().Throw<MetadataParseException>();
        database.KnownBashTags(MergeMode.WithoutUserMetadata).Should().Equal("C.Climate");
    }

    public class KnownBashTags
    {
        [Fact]
        public void ShouldNotAppendUserlistTagsWithoutUserMetadata()
        {
            var database = LoadedDatabase("bash_tags: [Relev, Delev]");

            database.KnownBashTags(MergeMode.WithoutUserMetadata).Should().Equal("C.Climate");
        }

        [Fact]
        public void ShouldAppendUserlistTagsAfterMasterlistTagsWithUserMetadataWithoutDeduplicating()
        {
            var database = LoadedDatabase("bash_tags: [C.Climate, Relev, Delev, Relev]");

            database.KnownBashTags(MergeMode.WithUserMetadata)
                .Should().Equal("C.Climate", "C.Climate", "Relev", "Delev", "Relev");
        }
    }

    public class GeneralMessages
    {
        [Fact]
        public void ShouldNotAppendUserlistMessagesWithoutUserMetadata()
        {
            var database = LoadedDatabase("globals: [{type: warn, content: user}]");

            var (messages, _) = database.GeneralMessages(MergeMode.WithoutUserMetadata, EvalMode.DoNotEvaluate);

            messages.Select(m => m.Content[0].Text).Should().Equal("A general message", "An unconditional message");
        }

        [Fact]
        public void ShouldAppendUserlistMessagesAfterMasterlistMessagesWithUserMetadata()
        {
            var database = LoadedDatabase("globals: [{type: warn, content: user}]");

            var (messages, unevaluated) = database.GeneralMessages(MergeMode.WithUserMetadata, EvalMode.DoNotEvaluate);

            messages.Select(m => m.Content[0].Text).Should().Equal("A general message", "An unconditional message", "user");
            unevaluated.Should().Be(0);
        }

        [Fact]
        public void ShouldDropConditionedMessagesAndCountThemWhenEvaluating()
        {
            var database = LoadedDatabase("globals: [{type: warn, content: user, condition: 'file(\"x.esp\")'}]");

            var (messages, unevaluated) = database.GeneralMessages(MergeMode.WithUserMetadata, EvalMode.Evaluate);

            messages.Select(m => m.Content[0].Text).Should().Equal("An unconditional message");
            unevaluated.Should().Be(2);
        }

        [Fact]
        public void UserGeneralMessagesShouldReturnOnlyTheUserlistMessages()
        {
            var database = LoadedDatabase("globals: [{type: warn, content: user}]");

            var (messages, _) = database.UserGeneralMessages(EvalMode.DoNotEvaluate);

            messages.Select(m => m.Content[0].Text).Should().Equal("user");
        }
    }

    public class Groups
    {
        [Fact]
        public void ShouldReturnTheDefaultGroupBeforeMetadataIsLoaded()
        {
            new LootDatabase().Groups(MergeMode.WithUserMetadata).Should().Equal(new Group());
        }

        [Fact]
        public void ShouldNotIncludeUserGroupsWithoutUserMetadata()
        {
            var database = LoadedDatabase("groups: [{name: group2, after: [default]}, {name: group3, after: [group1]}]");

            database.Groups(MergeMode.WithoutUserMetadata).Should().Equal(
                new Group(),
                new Group("group1"),
                new Group("group2") { AfterGroups = ["group1"] });
        }

        [Fact]
        public void ShouldMergeMasterlistAndUserlistGroupsAppendingAfterListsAndAddingNewGroups()
        {
            var database = LoadedDatabase("groups: [{name: group2, after: [default]}, {name: group3, after: [group1]}]");

            database.Groups(MergeMode.WithUserMetadata).Should().Equal(
                new Group(),
                new Group("group1"),
                new Group("group2") { AfterGroups = ["group1", "default"] },
                new Group("group3") { AfterGroups = ["group1"] });
        }

        [Fact]
        public void ShouldLetAUserDescriptionOverrideTheMasterlistOne()
        {
            var database = new LootDatabase();
            database.LoadMasterlist("groups: [{name: g, description: from masterlist}]");
            database.LoadUserlist("groups: [{name: g, description: from user}]");

            database.Groups(MergeMode.WithUserMetadata).Single(g => g.Name == "g").Description.Should().Be("from user");
        }

        [Fact]
        public void ShouldKeepTheMasterlistDescriptionWhenTheUserGroupHasNone()
        {
            var database = new LootDatabase();
            database.LoadMasterlist("groups: [{name: g, description: from masterlist}]");
            database.LoadUserlist("groups: [{name: g, after: [default]}]");

            var merged = database.Groups(MergeMode.WithUserMetadata).Single(g => g.Name == "g");

            merged.Description.Should().Be("from masterlist");
            merged.AfterGroups.Should().Equal("default");
        }

        [Fact]
        public void UserGroupsShouldNotIncludeMasterlistGroups()
        {
            LoadedDatabase().UserGroups.Should().Equal(new Group());
        }

        [Fact]
        public void UserGroupsShouldBeWhatTheUserlistDefines()
        {
            var database = LoadedDatabase("groups: [{name: group3, after: [group1]}]");

            database.UserGroups.Should().Equal(new Group(), new Group("group3") { AfterGroups = ["group1"] });
        }

        [Fact]
        public void GroupsPathShouldUseMasterlistAndUserMetadata()
        {
            var database = LoadedDatabase("groups: [{name: group3, after: [group2]}]");

            database.GroupsPath("group1", "group3").Should().Equal(
                new Vertex("group1", EdgeType.MasterlistLoadAfter),
                new Vertex("group2", EdgeType.UserLoadAfter),
                new Vertex("group3"));
        }

        [Fact]
        public void GroupsPathShouldBeEmptyWhenThereIsNoPath()
        {
            LoadedDatabase().GroupsPath("group2", "group1").Should().BeEmpty();
        }

        [Fact]
        public void GroupsPathShouldThrowForAnUndefinedGroup()
        {
            var path = () => LoadedDatabase().GroupsPath("group1", "missing");

            path.Should().Throw<GroupsPathException>();
        }
    }

    public class PluginMetadataLookup
    {
        private static IReadOnlyList<string> LoadAfterNames(PluginMetadata? metadata) =>
            metadata!.LoadAfter.Select(f => f.Name.Value).ToList();

        [Fact]
        public void ShouldReturnNullIfThePluginHasNoMetadata()
        {
            var (metadata, unevaluated) = new LootDatabase().PluginMetadata(BlankEsm, MergeMode.WithUserMetadata, EvalMode.DoNotEvaluate);

            metadata.Should().BeNull();
            unevaluated.Should().Be(0);
        }

        [Fact]
        public void ShouldReturnNullIfTheMergedMetadataHasOnlyAName()
        {
            var database = new LootDatabase();
            database.LoadUserlist("plugins: [{name: Blank.esm}]");

            database.PluginMetadata(BlankEsm, MergeMode.WithUserMetadata, EvalMode.DoNotEvaluate).Metadata.Should().BeNull();
        }

        [Fact]
        public void ShouldPutUserMetadataBeforeMasterlistMetadata()
        {
            var database = LoadedDatabase($"plugins: [{{name: {BlankEsm}, after: ['{BlankDifferentEsm}']}}]");

            var (metadata, _) = database.PluginMetadata(BlankEsm, MergeMode.WithUserMetadata, EvalMode.DoNotEvaluate);

            LoadAfterNames(metadata).Should().Equal(BlankDifferentEsm, "Oblivion.esm");
        }

        [Fact]
        public void ShouldPreferTheUserGroupWhenBothSetOne()
        {
            var database = new LootDatabase();
            database.LoadMasterlist("plugins: [{name: Blank.esm, group: late}]");
            database.LoadUserlist("plugins: [{name: Blank.esm, group: early}]");

            database.PluginMetadata(BlankEsm, MergeMode.WithUserMetadata, EvalMode.DoNotEvaluate).Metadata!.Group.Should().Be("early");
        }

        [Fact]
        public void ShouldReturnOnlyMasterlistMetadataWithoutUserMetadata()
        {
            var database = LoadedDatabase($"plugins: [{{name: {BlankEsm}, after: ['{BlankDifferentEsm}']}}]");

            var (metadata, _) = database.PluginMetadata(BlankEsm, MergeMode.WithoutUserMetadata, EvalMode.DoNotEvaluate);

            LoadAfterNames(metadata).Should().Equal("Oblivion.esm");
        }

        [Fact]
        public void ShouldMergeInMatchingRegexEntriesOfBothLists()
        {
            var database = new LootDatabase();
            database.LoadMasterlist("plugins: [{name: 'Blank.*\\.esm', after: [A.esm]}]");
            database.LoadUserlist("plugins: [{name: 'Bla.*', after: [B.esm]}]");

            var (metadata, _) = database.PluginMetadata(BlankEsm, MergeMode.WithUserMetadata, EvalMode.DoNotEvaluate);

            LoadAfterNames(metadata).Should().Equal("B.esm", "A.esm");
        }

        [Fact]
        public void ShouldKeepEverythingWhenNotEvaluating()
        {
            var (metadata, unevaluated) = LoadedDatabase().PluginMetadata(BlankEsm, MergeMode.WithUserMetadata, EvalMode.DoNotEvaluate);

            metadata!.Messages.Should().ContainSingle();
            metadata.Tags.Should().HaveCount(3);
            unevaluated.Should().Be(0);
        }

        [Fact]
        public void ShouldDropAConditionedMessageAndCountItWhenEvaluating()
        {
            var (metadata, unevaluated) = LoadedDatabase().PluginMetadata(BlankEsm, MergeMode.WithUserMetadata, EvalMode.Evaluate);

            metadata!.Messages.Should().BeEmpty();
            metadata.Tags.Should().HaveCount(3);
            unevaluated.Should().Be(1);
        }

        [Fact]
        public void ShouldDropConditionedFilesTagsAndEveryCleaningEntryAndCountEachWhenEvaluating()
        {
            var database = new LootDatabase();
            database.LoadMasterlist("""
                plugins:
                  - name: A.esp
                    after:
                      - {name: B.esp, condition: 'file("B.esp")'}
                      - C.esp
                    req:
                      - {name: D.esp, condition: 'file("D.esp")'}
                    inc:
                      - {name: E.esp, condition: 'file("E.esp")'}
                    tag:
                      - {name: Relev, condition: 'file("x")'}
                      - Delev
                    msg:
                      - {type: say, content: hi, condition: 'file("y")'}
                    dirty:
                      - {crc: 0x1, util: TES4Edit, condition: 'file("z")'}
                    clean:
                      - {crc: 0x2, util: TES4Edit, condition: 'file("w")'}
                      - {crc: 0x3, util: TES4Edit}
                """);

            var (metadata, unevaluated) = database.PluginMetadata("A.esp", MergeMode.WithUserMetadata, EvalMode.Evaluate);

            unevaluated.Should().Be(8);
            LoadAfterNames(metadata).Should().Equal("C.esp");
            metadata!.Requirements.Should().BeEmpty();
            metadata.Incompatibilities.Should().BeEmpty();
            metadata.Tags.Select(t => t.Name).Should().Equal("Delev");
            metadata.Messages.Should().BeEmpty();
            metadata.DirtyInfo.Should().BeEmpty();
            metadata.CleanInfo.Should().BeEmpty();
        }

        [Fact]
        public void ShouldReturnNullAndCountWhenEvaluatingLeavesOnlyAName()
        {
            var database = new LootDatabase();
            database.LoadMasterlist("plugins: [{name: A.esp, after: [{name: B.esp, condition: 'file(\"B.esp\")'}]}]");

            var (metadata, unevaluated) = database.PluginMetadata("A.esp", MergeMode.WithUserMetadata, EvalMode.Evaluate);

            metadata.Should().BeNull();
            unevaluated.Should().Be(1);
        }

        [Fact]
        public void ShouldDropConditionedUserMetadataWhenEvaluating()
        {
            var database = LoadedDatabase(
                $"plugins: [{{name: {BlankEsm}, msg: [{{type: say, content: user, condition: 'file(\"x.esp\")'}}]}}]");

            var (metadata, unevaluated) = database.PluginMetadata(BlankEsm, MergeMode.WithUserMetadata, EvalMode.Evaluate);

            metadata!.Messages.Should().BeEmpty();
            unevaluated.Should().Be(2);
        }
    }

    public class PluginUserMetadataLookup
    {
        [Fact]
        public void ShouldReturnNullIfThePluginHasNoUserMetadata()
        {
            LoadedDatabase().PluginUserMetadata(BlankEsm, EvalMode.DoNotEvaluate).Metadata.Should().BeNull();
        }

        [Fact]
        public void ShouldReturnNullIfTheUserMetadataHasOnlyAName()
        {
            var database = new LootDatabase();
            database.LoadUserlist("plugins: [{name: Blank.esm}]");

            database.PluginUserMetadata(BlankEsm, EvalMode.DoNotEvaluate).Metadata.Should().BeNull();
        }

        [Fact]
        public void ShouldReturnOnlyUserMetadata()
        {
            var database = LoadedDatabase($"plugins: [{{name: {BlankEsm}, after: ['{BlankDifferentEsm}']}}]");

            var (metadata, _) = database.PluginUserMetadata(BlankEsm, EvalMode.DoNotEvaluate);

            metadata!.LoadAfter.Select(f => f.Name.Value).Should().Equal(BlankDifferentEsm);
        }

        [Fact]
        public void ShouldDropConditionedEntriesAndCountThemWhenEvaluating()
        {
            var database = LoadedDatabase(
                $"plugins: [{{name: {BlankEsm}, msg: [{{type: say, content: user, condition: 'file(\"x.esp\")'}}], tag: [Relev]}}]");

            var (metadata, unevaluated) = database.PluginUserMetadata(BlankEsm, EvalMode.Evaluate);

            metadata!.Messages.Should().BeEmpty();
            metadata.Tags.Should().ContainSingle();
            unevaluated.Should().Be(1);
        }
    }

    public class WithAnEvaluator
    {
        private static LootDatabase Database(FakeConditionEvaluator evaluator, string masterlist, string? userlist = null)
        {
            var database = new LootDatabase(evaluator);
            database.LoadMasterlist(masterlist);
            if (userlist is not null) database.LoadUserlist(userlist);
            return database;
        }

        private const string Conditioned = """
            plugins:
              - name: A.esp
                after:
                  - {name: B.esp, condition: 'file("B.esp")'}
                  - {name: C.esp, condition: 'file("C.esp")'}
                  - {name: D.esp, condition: 'file("D.esp")'}
                req:
                  - {name: E.esp, condition: 'file("E.esp")'}
                inc:
                  - {name: F.esp, condition: 'file("F.esp")'}
                tag:
                  - {name: Relev, condition: 'file("Relev")'}
                  - Delev
                msg:
                  - {type: say, content: hi, condition: 'file("msg")'}
            """;

        [Fact]
        public void ShouldKeepWhatHoldsDropWhatDoesNotAndCountOnlyWhatCannotBeEvaluated()
        {
            var evaluator = new FakeConditionEvaluator(new Dictionary<string, bool>
            {
                ["file(\"B.esp\")"] = true,
                ["file(\"C.esp\")"] = false,
                ["file(\"E.esp\")"] = true,
                ["file(\"F.esp\")"] = false,
                ["file(\"Relev\")"] = false,
                ["file(\"msg\")"] = true,
            });
            evaluator.Failing.Add("file(\"D.esp\")");

            var (metadata, unevaluated) = Database(evaluator, Conditioned)
                .PluginMetadata("A.esp", MergeMode.WithUserMetadata, EvalMode.Evaluate);

            metadata!.LoadAfter.Select(f => f.Name.Value).Should().Equal("B.esp");
            metadata.Requirements.Select(f => f.Name.Value).Should().Equal("E.esp");
            metadata.Incompatibilities.Should().BeEmpty();
            metadata.Tags.Select(t => t.Name).Should().Equal("Delev");
            metadata.Messages.Should().ContainSingle();
            unevaluated.Should().Be(1);
        }

        [Fact]
        public void ShouldNotAskWhenNotEvaluating()
        {
            var evaluator = new FakeConditionEvaluator();

            var (metadata, unevaluated) = Database(evaluator, Conditioned)
                .PluginMetadata("A.esp", MergeMode.WithUserMetadata, EvalMode.DoNotEvaluate);

            metadata!.LoadAfter.Should().HaveCount(3);
            unevaluated.Should().Be(0);
            evaluator.Asked.Should().BeEmpty();
        }

        [Fact]
        public void ShouldEvaluateUserMetadataAndGeneralMessages()
        {
            var evaluator = new FakeConditionEvaluator(new Dictionary<string, bool>
            {
                ["file(\"a\")"] = true,
                ["file(\"b\")"] = false,
            });
            var database = Database(evaluator,
                "globals: [{type: say, content: kept, condition: 'file(\"a\")'}, {type: say, content: dropped, condition: 'file(\"b\")'}]",
                "globals: [{type: say, content: user, condition: 'file(\"a\")'}]");

            var (messages, unevaluated) = database.GeneralMessages(MergeMode.WithUserMetadata, EvalMode.Evaluate);
            var (userMessages, _) = database.UserGeneralMessages(EvalMode.Evaluate);

            messages.Should().HaveCount(2);
            userMessages.Should().ContainSingle();
            unevaluated.Should().Be(0);
        }

        [Fact]
        public void ShouldKeepACleaningEntryWhenThePluginsChecksumMatches()
        {
            var evaluator = new FakeConditionEvaluator(new Dictionary<string, bool>
            {
                ["checksum(\"A.esp\", 0000002A)"] = true,
                ["checksum(\"A.esp\", 000000FF)"] = false,
            });
            var database = Database(evaluator, """
                plugins:
                  - name: A.esp
                    dirty:
                      - {crc: 0x2A, util: TES4Edit, itm: 3}
                      - {crc: 0xFF, util: TES4Edit}
                    clean:
                      - {crc: 0x2A, util: TES4Edit}
                """);

            var (metadata, unevaluated) = database.PluginMetadata("A.esp", MergeMode.WithUserMetadata, EvalMode.Evaluate);

            metadata!.DirtyInfo.Select(d => d.Crc).Should().Equal(0x2Au);
            metadata.CleanInfo.Select(d => d.Crc).Should().Equal(0x2Au);
            unevaluated.Should().Be(0);
        }

        [Fact]
        public void ShouldRequireTheCleaningEntrysOwnConditionAsWell()
        {
            var evaluator = new FakeConditionEvaluator(new Dictionary<string, bool>
            {
                ["checksum(\"A.esp\", 0000002A) and (file(\"x\"))"] = false,
            });
            evaluator.Answers["checksum(\"A.esp\", 0000002A) and (file(\"y\"))"] = true;
            var database = Database(evaluator, """
                plugins:
                  - name: A.esp
                    dirty:
                      - {crc: 0x2A, util: TES4Edit, condition: 'file("x")'}
                      - {crc: 0x2A, util: Other, condition: 'file("y")'}
                """);

            var (metadata, _) = database.PluginMetadata("A.esp", MergeMode.WithUserMetadata, EvalMode.Evaluate);

            metadata!.DirtyInfo.Select(d => d.CleaningUtility).Should().Equal("Other");
        }

        [Fact]
        public void ShouldCountACleaningEntryWhoseChecksumCannotBeRead()
        {
            var evaluator = new FakeConditionEvaluator();
            evaluator.Failing.Add("checksum(\"A.esp\", 0000002A)");
            var database = Database(evaluator, "plugins: [{name: A.esp, dirty: [{crc: 0x2A, util: TES4Edit}]}]");

            var (metadata, unevaluated) = database.PluginMetadata("A.esp", MergeMode.WithUserMetadata, EvalMode.Evaluate);

            metadata.Should().BeNull();
            unevaluated.Should().Be(1);
        }

        [Fact]
        public void ShouldOnlyEvaluateTheChecksumWhenAPluginNameHoldsConditionSyntax()
        {
            using var root = new TempDirectory();
            root.Write("x");
            var data = root.CreateDirectory("Data");
            var database = new LootDatabase(new ConditionEvaluator(new FakeConditionContext(data)));
            database.LoadMasterlist("""
                plugins:
                  - name: 'A") or file("../x'
                    dirty: [{crc: 0x2A, util: TES4Edit}]
                """);

            var (unevaluatedEntry, _) = database.PluginMetadata("A\") or file(\"../x", MergeMode.WithUserMetadata, EvalMode.DoNotEvaluate);
            unevaluatedEntry!.DirtyInfo.Should().ContainSingle();

            var (metadata, unevaluated) = database.PluginMetadata("A\") or file(\"../x", MergeMode.WithUserMetadata, EvalMode.Evaluate);

            metadata.Should().BeNull();
            unevaluated.Should().Be(0);
        }

        [Fact]
        public void ShouldNotLetAPluginNameThatParsesAsMoreConditionsPassTheChecksum()
        {
            using var root = new TempDirectory();
            var data = root.CreateDirectory("Data");
            root.Write("Data/x");
            var database = new LootDatabase(new ConditionEvaluator(new FakeConditionContext(data)));
            const string name = "A\", 0000002A) or file(\"x\") or checksum(\"B";
            database.LoadMasterlist($$"""
                plugins:
                  - name: '{{name}}'
                    dirty: [{crc: 0x2A, util: TES4Edit}]
                """);

            var (metadata, _) = database.PluginMetadata(name, MergeMode.WithUserMetadata, EvalMode.Evaluate);

            metadata.Should().BeNull();
        }

        [Fact]
        public void EvaluateShouldReportNullWithoutAnEvaluator()
        {
            new LootDatabase().Evaluate("file(\"a\")").Should().BeNull();
        }

        [Fact]
        public void EvaluateShouldAnswerTrueAndFalseFromTheEvaluator()
        {
            var evaluator = new FakeConditionEvaluator(new Dictionary<string, bool> { ["file(\"a\")"] = true, ["file(\"b\")"] = false });
            var database = new LootDatabase(evaluator);

            database.Evaluate("file(\"a\")").Should().BeTrue();
            database.Evaluate("file(\"b\")").Should().BeFalse();
        }

        [Fact]
        public void EvaluateShouldReportNullWhenTheEvaluatorThrows()
        {
            var evaluator = new FakeConditionEvaluator();
            evaluator.Failing.Add("file(\"a\")");

            new LootDatabase(evaluator).Evaluate("file(\"a\")").Should().BeNull();
        }

        [Fact]
        public void EvaluateShouldReportNullWhenTheTextDoesNotParse()
        {
            var evaluator = new ConditionEvaluator(new FakeConditionContext("/nonexistent"));

            new LootDatabase(evaluator).Evaluate("file(").Should().BeNull();
        }
    }
}
