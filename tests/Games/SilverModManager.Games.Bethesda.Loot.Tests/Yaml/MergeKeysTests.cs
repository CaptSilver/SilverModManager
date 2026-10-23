using NexusMods.Games.Bethesda.Loot.Metadata;
using NexusMods.Games.Bethesda.Loot.Metadata.Yaml;
using YamlDotNet.RepresentationModel;

namespace NexusMods.Games.Bethesda.Loot.Tests.Yaml;

// libloot's yaml/merge.rs tests, plus the merge behaviour its metadata_document tests rely on.
public class MergeKeysTests
{
    private static YamlMappingNode Process(string yaml) => (YamlMappingNode)MergeKeys.Process(TestYaml.Parse(yaml));

    private static string Scalar(YamlMappingNode mapping, string key) =>
        ((YamlScalarNode)mapping.Children[new YamlScalarNode(key)]).Value!;

    [Fact]
    public void ShouldErrorIfMergeKeyValueHasASingleValueThatIsNotAMapping()
    {
        var yaml = TestYaml.Parse("""

            - &anchor1 test
            - <<: *anchor1
              value: test-value-2
            """);

        // libloot reports the position of the alias, line 3. An alias is the anchored node itself
        // here, so this reports where that node starts: the anchor, on line 2.
        Action act = () => MergeKeys.Process(yaml);

        act.Should().Throw<YamlMergeKeyException>()
            .WithMessage("invalid YAML merge key value at line 2 column 3: test");
    }

    [Fact]
    public void ShouldErrorIfMergeKeyValueIsASequenceOfNonMappings()
    {
        var yaml = TestYaml.Parse("""

            - &anchor1 {key: test-key}
            - &anchor2 test
            - <<: [*anchor1, *anchor2]
              value: test-value-2
            """);

        Action act = () => MergeKeys.Process(yaml);

        act.Should().Throw<YamlMergeKeyException>()
            .WithMessage("invalid YAML merge key value at line 3 column 3: test");
    }

    [Fact]
    public void ShouldReportTheLocationOfAnInlineBadValueExactly()
    {
        var yaml = TestYaml.Parse("common:\n  - <<: 1\n");

        Action act = () => MergeKeys.Process(yaml);

        act.Should().Throw<YamlMergeKeyException>()
            .WithMessage("invalid YAML merge key value at line 2 column 9: 1");
    }

    [Fact]
    public void ShouldMergeAMappingIntoItsParent()
    {
        var root = Process("""
            base: &base {a: 1, b: 2}
            item:
              <<: *base
              c: 3
            """);

        var item = (YamlMappingNode)root.Children[new YamlScalarNode("item")];
        Scalar(item, "a").Should().Be("1");
        Scalar(item, "b").Should().Be("2");
        Scalar(item, "c").Should().Be("3");
        item.Children.Keys.Select(k => ((YamlScalarNode)k).Value).Should().NotContain("<<");
    }

    [Fact]
    public void AnOwnKeyShouldWinOverAMergedOne()
    {
        var root = Process("""
            base: &base {a: from-base, b: from-base}
            item:
              <<: *base
              a: own
            """);

        var item = (YamlMappingNode)root.Children[new YamlScalarNode("item")];
        Scalar(item, "a").Should().Be("own");
        Scalar(item, "b").Should().Be("from-base");
    }

    [Fact]
    public void ASequenceOfMappingsShouldMergeInOrderWithEarlierEntriesWinning()
    {
        var root = Process("""
            one: &one {a: one, b: one}
            two: &two {a: two, b: two, c: two}
            item:
              <<: [*one, *two]
            """);

        var item = (YamlMappingNode)root.Children[new YamlScalarNode("item")];
        Scalar(item, "a").Should().Be("one");
        Scalar(item, "b").Should().Be("one");
        Scalar(item, "c").Should().Be("two");
    }

    [Fact]
    public void ShouldProcessNestedSequencesAndMappings()
    {
        var root = Process("""
            base: &base {a: 1}
            list:
              - - <<: *base
                  x: 1
              - nested:
                  <<: *base
            """);

        var list = (YamlSequenceNode)root.Children[new YamlScalarNode("list")];
        var first = (YamlMappingNode)((YamlSequenceNode)list.Children[0]).Children[0];
        Scalar(first, "a").Should().Be("1");
        var second = (YamlMappingNode)((YamlMappingNode)list.Children[1]).Children[new YamlScalarNode("nested")];
        Scalar(second, "a").Should().Be("1");
    }

    [Fact]
    public void ShouldResolveAnAnchoredMappingThatIsItselfBuiltFromAMerge()
    {
        // The anchored mapping is the same node at every alias, so it is merged into two
        // targets after being processed once; each target's own keys still win.
        var root = Process("""
            base: &base {a: base, b: base}
            derived: &derived
              <<: *base
              b: derived
            first:
              <<: *derived
              a: first
            second:
              <<: *derived
            """);

        var first = (YamlMappingNode)root.Children[new YamlScalarNode("first")];
        Scalar(first, "a").Should().Be("first");
        Scalar(first, "b").Should().Be("derived");

        var second = (YamlMappingNode)root.Children[new YamlScalarNode("second")];
        Scalar(second, "a").Should().Be("base");
        Scalar(second, "b").Should().Be("derived");
    }

    [Fact]
    public void ShouldTreatAQuotedMergeKeyAsAMergeKey()
    {
        var root = Process("{'<<': {a: 1}, b: 2}");

        Scalar(root, "a").Should().Be("1");
        Scalar(root, "b").Should().Be("2");
    }

    [Fact]
    public void ShouldReturnScalarsUnchanged()
    {
        var node = TestYaml.Parse("text");

        MergeKeys.Process(node).Should().BeSameAs(node);
    }
}
