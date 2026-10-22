using NexusMods.Games.Bethesda.Loot.Metadata;
using static NexusMods.Games.Bethesda.Loot.Tests.Metadata.TestNames;

namespace NexusMods.Games.Bethesda.Loot.Tests.Metadata;

// Test names follow libloot's metadata/plugin_metadata.rs; its YAML and emitter tests are not ported here.
public class PluginMetadataTests
{
    public class NameMatchesTests
    {
        [Fact]
        public void ShouldUseCaseInsensitiveComparisonForNonRegexNames()
        {
            var plugin = new PluginMetadata("BLANK.ESM");

            plugin.NameMatches("blank.esm").Should().BeTrue();
            plugin.NameMatches("other.esm").Should().BeFalse();
        }

        [Fact]
        public void ShouldTreatGivenPluginNameAsLiteral()
        {
            new PluginMetadata("Blank.esm").NameMatches(".+").Should().BeFalse();
        }

        [Fact]
        public void ShouldUseCaseInsensitiveRegexMatchingForARegexName()
        {
            new PluginMetadata("Blank.ES(m|p)").NameMatches("blank.esm").Should().BeTrue();
        }
    }

    public class MergeMetadataTests
    {
        [Fact]
        public void ShouldNotChangeName()
        {
            var merged = new PluginMetadata(BlankEsm).MergeMetadata(new PluginMetadata(BlankDifferentEsm) { Group = "g" });

            merged.Name.Should().Be(BlankEsm);
        }

        [Fact]
        public void ShouldNotUseOtherGroupIfCurrentGroupIsSet()
        {
            var plugin1 = new PluginMetadata(BlankEsm) { Group = "group1" };
            var plugin2 = new PluginMetadata(BlankEsm) { Group = "group2" };

            var merged = plugin1.MergeMetadata(plugin2);
            merged.Group.Should().Be("group1");

            merged = merged.MergeMetadata(plugin2 with { Group = null });
            merged.Group.Should().Be("group1");
        }

        [Fact]
        public void ShouldUseOtherGroupIfCurrentGroupIsNone()
        {
            var merged = new PluginMetadata(BlankEsm).MergeMetadata(new PluginMetadata(BlankEsm) { Group = "group2" });

            merged.Group.Should().Be("group2");
        }

        [Fact]
        public void ShouldMergeLoadAfterFiles()
        {
            var file1 = new File(BlankDifferentEsm);
            var file2 = new File(BlankEsp);
            var file3 = new File(BlankDifferentEsp);

            var merged = new PluginMetadata(BlankEsm) { LoadAfter = [file1, file2] }
                .MergeMetadata(new PluginMetadata(BlankEsm) { LoadAfter = [file1, file3] });

            merged.LoadAfter.Should().Equal(file1, file2, file3);
        }

        [Fact]
        public void ShouldMergeRequirements()
        {
            var file1 = new File(BlankDifferentEsm);
            var file2 = new File(BlankEsp);
            var file3 = new File(BlankDifferentEsp);

            var merged = new PluginMetadata(BlankEsm) { Requirements = [file1, file2] }
                .MergeMetadata(new PluginMetadata(BlankEsm) { Requirements = [file1, file3] });

            merged.Requirements.Should().Equal(file1, file2, file3);
        }

        [Fact]
        public void ShouldMergeIncompatibilities()
        {
            var file1 = new File(BlankDifferentEsm);
            var file2 = new File(BlankEsp);
            var file3 = new File(BlankDifferentEsp);

            var merged = new PluginMetadata(BlankEsm) { Incompatibilities = [file1, file2] }
                .MergeMetadata(new PluginMetadata(BlankEsm) { Incompatibilities = [file1, file3] });

            merged.Incompatibilities.Should().Equal(file1, file2, file3);
        }

        [Fact]
        public void ShouldMergeMessages()
        {
            var message1 = new Message(MessageType.Say, "content1");
            var message2 = new Message(MessageType.Say, "content2");
            var message3 = new Message(MessageType.Say, "content3");

            var merged = new PluginMetadata(BlankEsm) { Messages = [message1, message2] }
                .MergeMetadata(new PluginMetadata(BlankEsm) { Messages = [message1, message3] });

            merged.Messages.Should().Equal(message1, message2, message1, message3);
        }

        [Fact]
        public void ShouldMergeTags()
        {
            var tag1 = new Tag("Relev");
            var tag2 = new Tag("Delev");
            var tag3 = new Tag("Relev", TagSuggestion.Removal);

            var merged = new PluginMetadata(BlankEsm) { Tags = [tag1, tag2] }
                .MergeMetadata(new PluginMetadata(BlankEsm) { Tags = [tag1, tag3] });

            merged.Tags.Should().Equal(tag1, tag2, tag3);
        }

        [Fact]
        public void ShouldMergeDirtyInfo()
        {
            var data1 = new PluginCleaningData(0x1234_5678, "util1");
            var data2 = new PluginCleaningData(0xDEAD_BEEF, "util2");
            var data3 = new PluginCleaningData(0xFEED_CAFE, "util3");

            var merged = new PluginMetadata(BlankEsm) { DirtyInfo = [data1, data2] }
                .MergeMetadata(new PluginMetadata(BlankEsm) { DirtyInfo = [data1, data3] });

            merged.DirtyInfo.Should().Equal(data1, data2, data3);
        }

        [Fact]
        public void ShouldMergeCleanInfo()
        {
            var data1 = new PluginCleaningData(0x1234_5678, "util1");
            var data2 = new PluginCleaningData(0xDEAD_BEEF, "util2");
            var data3 = new PluginCleaningData(0xFEED_CAFE, "util3");

            var merged = new PluginMetadata(BlankEsm) { CleanInfo = [data1, data2] }
                .MergeMetadata(new PluginMetadata(BlankEsm) { CleanInfo = [data1, data3] });

            merged.CleanInfo.Should().Equal(data1, data2, data3);
        }

        [Fact]
        public void ShouldMergeLocations()
        {
            var location1 = new Location("url1");
            var location2 = new Location("url2");
            var location3 = new Location("url3");

            var merged = new PluginMetadata(BlankEsm) { Locations = [location1, location2] }
                .MergeMetadata(new PluginMetadata(BlankEsm) { Locations = [location1, location3] });

            merged.Locations.Should().Equal(location1, location2, location3);
        }

        [Fact]
        public void ShouldAppendEveryDuplicateInTheSourceBecauseElementsAreTestedAgainstTheOriginalTarget()
        {
            var a = new File("a.esp");

            var merged = new PluginMetadata(BlankEsm)
                .MergeMetadata(new PluginMetadata(BlankEsm) { LoadAfter = [a, a] });

            merged.LoadAfter.Should().Equal(a, a);
        }

        [Fact]
        public void ShouldNotAppendSourceElementsThatMatchTheTargetCaseInsensitivelyByFilename()
        {
            var merged = new PluginMetadata(BlankEsm) { LoadAfter = [new File("a.esp")] }
                .MergeMetadata(new PluginMetadata(BlankEsm) { LoadAfter = [new File("A.ESP")] });

            merged.LoadAfter.Should().HaveCount(1);
        }

        [Fact]
        public void ShouldChangeNothingIfOtherHasOnlyAName()
        {
            var plugin = new PluginMetadata(BlankEsm) { Group = "g", LoadAfter = [new File("a.esp")] };

            plugin.MergeMetadata(new PluginMetadata(BlankDifferentEsm)).Should().Be(plugin);
        }

        [Fact]
        public void ShouldNotModifyEitherInput()
        {
            var plugin1 = new PluginMetadata(BlankEsm) { Tags = [new Tag("a")] };
            var plugin2 = new PluginMetadata(BlankEsm) { Tags = [new Tag("b")] };

            plugin1.MergeMetadata(plugin2);

            plugin1.Tags.Should().Equal(new Tag("a"));
            plugin2.Tags.Should().Equal(new Tag("b"));
        }
    }

    [Fact]
    public void UnsetGroup_ShouldSetGroupToNone()
    {
        var plugin = new PluginMetadata(BlankEsm) { Group = "group1" } with { Group = null };

        plugin.Group.Should().BeNull();
    }

    public class HasNameOnlyTests
    {
        [Fact]
        public void ShouldBeTrueIfOnlyANameIsSet()
        {
            new PluginMetadata(BlankEsm).HasNameOnly.Should().BeTrue();
        }

        [Fact]
        public void ShouldBeFalseIfAGroupIsSet()
        {
            (new PluginMetadata(BlankEsm) { Group = "group1" }).HasNameOnly.Should().BeFalse();
        }

        [Fact]
        public void ShouldBeFalseIfLoadAfterFilesAreSet()
        {
            (new PluginMetadata(BlankEsm) { LoadAfter = [new File("a")] }).HasNameOnly.Should().BeFalse();
        }

        [Fact]
        public void ShouldBeFalseIfRequirementsAreSet()
        {
            (new PluginMetadata(BlankEsm) { Requirements = [new File("a")] }).HasNameOnly.Should().BeFalse();
        }

        [Fact]
        public void ShouldBeFalseIfIncompatibilitiesAreSet()
        {
            (new PluginMetadata(BlankEsm) { Incompatibilities = [new File("a")] }).HasNameOnly.Should().BeFalse();
        }

        [Fact]
        public void ShouldBeFalseIfMessagesAreSet()
        {
            (new PluginMetadata(BlankEsm) { Messages = [new Message(MessageType.Say, "a")] }).HasNameOnly.Should().BeFalse();
        }

        [Fact]
        public void ShouldBeFalseIfTagsAreSet()
        {
            (new PluginMetadata(BlankEsm) { Tags = [new Tag("a")] }).HasNameOnly.Should().BeFalse();
        }

        [Fact]
        public void ShouldBeFalseIfDirtyInfoIsSet()
        {
            (new PluginMetadata(BlankEsm) { DirtyInfo = [new PluginCleaningData(1, "u")] }).HasNameOnly.Should().BeFalse();
        }

        [Fact]
        public void ShouldBeFalseIfCleanInfoIsSet()
        {
            (new PluginMetadata(BlankEsm) { CleanInfo = [new PluginCleaningData(1, "u")] }).HasNameOnly.Should().BeFalse();
        }

        [Fact]
        public void ShouldBeFalseIfLocationsAreSet()
        {
            (new PluginMetadata(BlankEsm) { Locations = [new Location("a")] }).HasNameOnly.Should().BeFalse();
        }
    }

    public class IsRegexPluginTests
    {
        [Fact]
        public void ShouldBeFalseForAnEmptyName()
        {
            new PluginMetadata("").IsRegexPlugin.Should().BeFalse();
        }

        [Fact]
        public void ShouldBeFalseForAnExactPluginName()
        {
            new PluginMetadata(BlankEsm).IsRegexPlugin.Should().BeFalse();
        }

        [Theory]
        [InlineData(":", "colon")]
        [InlineData("\\", "backslash")]
        [InlineData("*", "asterisk")]
        [InlineData("?", "question mark")]
        [InlineData("|", "vertical bar")]
        public void ShouldBeTrueIfThePluginNameContainsARegexCharacter(string c, string description)
        {
            new PluginMetadata($"Blank{c}.esm").IsRegexPlugin.Should().BeTrue("the name contains a {0}", description);
        }
    }

    public class FilterByConstraintsTests
    {
        [Fact]
        public void ShouldDropLoadAfterFilesAndRequirementsWhoseConstraintIsFalse()
        {
            var plugin = new PluginMetadata(BlankEsm)
            {
                LoadAfter = [new File("a") { Constraint = "yes" }, new File("b") { Constraint = "no" }, new File("c")],
                Requirements = [new File("d") { Constraint = "no" }, new File("e") { Constraint = "yes" }],
            };

            var filtered = plugin.FilterByConstraints(c => c == "yes");

            filtered.LoadAfter.Select(f => f.Name.Value).Should().Equal("a", "c");
            filtered.Requirements.Select(f => f.Name.Value).Should().Equal("e");
        }

        [Fact]
        public void ShouldNotEvaluateFilesWithoutAConstraint()
        {
            var plugin = new PluginMetadata(BlankEsm) { LoadAfter = [new File("a") { Condition = "never evaluated" }] };

            var filtered = plugin.FilterByConstraints(_ => throw new InvalidOperationException());

            filtered.LoadAfter.Should().HaveCount(1);
        }

        [Fact]
        public void ShouldLeaveIncompatibilitiesAndOtherFieldsAlone()
        {
            var incompatible = new File("a") { Constraint = "no" };
            var plugin = new PluginMetadata(BlankEsm)
            {
                Group = "g",
                Incompatibilities = [incompatible],
                Tags = [new Tag("t")],
            };

            var filtered = plugin.FilterByConstraints(_ => false);

            filtered.Should().Be(plugin);
        }
    }

    [Fact]
    public void Constructor_ShouldTrimDotGhostFromTheName()
    {
        new PluginMetadata("Blank.esp.ghost").Name.Should().Be("Blank.esp");
    }

    [Fact]
    public void Constructor_ShouldThrowIfARegexNameIsInvalid()
    {
        var act = () => new PluginMetadata("Blank(.esp|");

        act.Should().Throw<InvalidPluginNameException>();
    }

    [Fact]
    public void Equality_ShouldCompareTheNameCaseSensitivelyAndAllFields()
    {
        new PluginMetadata("a.esp") { Group = "g" }.Should().Be(new PluginMetadata("a.esp") { Group = "g" });
        new PluginMetadata("a.esp").Should().NotBe(new PluginMetadata("A.esp"));
        new PluginMetadata("a.esp") { Group = "g" }.Should().NotBe(new PluginMetadata("a.esp"));
    }
}
