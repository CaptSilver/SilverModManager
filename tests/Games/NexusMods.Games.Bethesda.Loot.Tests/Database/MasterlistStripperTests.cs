using NexusMods.Games.Bethesda.Loot.Database;
using NexusMods.Games.Bethesda.Loot.Metadata;

namespace NexusMods.Games.Bethesda.Loot.Tests.Database;

public class MasterlistStripperTests
{
    private const string Masterlist = """
        prelude:
          - &warning
            type: warn
            content: Careful.
            condition: 'file("a.esp")'

        groups:
          - name: late
            after: [default]

        globals:
          - type: say
            content: Hello.
            condition: 'active("a.esp")'

        plugins:
          - name: A.esp
            after:
              - name: B.esp
                condition: 'file("B.esp")'
                constraint: 'file("C.esp")'
                detail:
                  - text: Needs B.
                    lang: en
              - C.esp
            req:
              - name: D.esp
                constraint: 'file("D.esp")'
            msg:
              - *warning
              - type: say
                content: Plain.
            tag:
              - name: Delev
                condition: 'file("E.esp")'
            dirty:
              - crc: 0x1
                util: tool
                condition: 'file("F.esp")'
        """;

    [Fact]
    public void ShouldRemoveConditionsAndConstraintsAtEveryDepth()
    {
        var stripped = MasterlistStripper.Strip(Masterlist);

        stripped.Should().NotContain("condition");
        stripped.Should().NotContain("constraint");

        var document = MetadataDocument.Load(stripped);
        var plugin = document.Plugins[new Filename("A.esp")];
        plugin.LoadAfter.Select(f => f.Name.Value).Should().Equal("B.esp", "C.esp");
        plugin.LoadAfter[0].Condition.Should().BeNull();
        plugin.LoadAfter[0].Constraint.Should().BeNull();
        plugin.LoadAfter[0].Detail.Should().Equal(new MessageContent("Needs B."));
        plugin.Requirements[0].Constraint.Should().BeNull();
        plugin.Tags[0].Condition.Should().BeNull();
        plugin.DirtyInfo[0].Condition.Should().BeNull();
        document.Globals[0].Condition.Should().BeNull();
    }

    [Fact]
    public void ShouldRemoveAConditionFromAnAnchoredNodeEverywhereItIsUsed()
    {
        var stripped = MasterlistStripper.Strip(Masterlist);

        var plugin = MetadataDocument.Load(stripped).Plugins[new Filename("A.esp")];
        plugin.Messages[0].Should().Be(new Message(MessageType.Warn, "Careful."));
    }

    [Fact]
    public void ShouldKeepEverythingElse()
    {
        var document = MetadataDocument.Load(MasterlistStripper.Strip(Masterlist));

        document.Groups.Select(g => g.Name).Should().Equal("default", "late");
        document.Groups[1].AfterGroups.Should().Equal("default");
        document.Globals.Should().Equal(new Message(MessageType.Say, "Hello."));
        var plugin = document.Plugins[new Filename("A.esp")];
        plugin.Messages[1].Should().Be(new Message(MessageType.Say, "Plain."));
        plugin.Tags.Should().Equal(new Tag("Delev"));
        plugin.DirtyInfo.Should().Equal(new PluginCleaningData(1, "tool"));
    }

    [Fact]
    public void ShouldReturnTheSameTextForTheSameInput()
    {
        MasterlistStripper.Strip(Masterlist).Should().Be(MasterlistStripper.Strip(Masterlist));
    }

    [Fact]
    public void ShouldBeIdempotent()
    {
        var once = MasterlistStripper.Strip(Masterlist);

        MasterlistStripper.Strip(once).Should().Be(once);
    }

    [Fact]
    public void ShouldKeepMergeKeysSoTheLoaderStillResolvesThem()
    {
        var stripped = MasterlistStripper.Strip("""
            prelude:
              - &base
                type: say
                content: Base.
                condition: 'file("x")'
            globals:
              - <<: *base
                content: Own.
            """);

        MetadataDocument.Load(stripped).Globals.Should().Equal(new Message(MessageType.Say, "Own."));
    }

    [Fact]
    public void ShouldLeaveATextWithNothingToStripSemanticallyUnchanged()
    {
        var text = "plugins:\n  - name: A.esp\n    after: [B.esp]\n";

        var document = MetadataDocument.Load(MasterlistStripper.Strip(text));

        document.Plugins[new Filename("A.esp")].LoadAfter.Should().Equal(new File("B.esp"));
    }

    [Fact]
    public void ShouldErrorOnInvalidYaml()
    {
        Action act = () => MasterlistStripper.Strip("a: [1, 2\nb: 3");

        act.Should().Throw<MetadataParseException>();
    }

    [Fact]
    public void ShouldReturnAnEmptyStringForAnEmptyInput()
    {
        MasterlistStripper.Strip("").Should().BeEmpty();
    }
}
