using FluentAssertions;

namespace SilverModManager.Games.Bethesda.LoadOrder.Tests.Records;

// Follows esplugin's resolve_namespaced_ids and the Morrowind override and overlap tests in plugin.rs.
public class NamedRecordIdsTests
{
    private static ulong[] Ids(params string[] names) => [.. names.Select(n => PluginRecordWalker.NamedId("GMST", [.. System.Text.Encoding.ASCII.GetBytes(n), 0])).Order()];

    [Fact]
    public void ARecordAMasterAlsoHasIsAnOverride()
    {
        var ids = PluginRecordIds.ResolveNamed(Ids("a", "b", "c"), [Ids("b", "x"), Ids("c")]);

        ids.Count.Should().Be(3);
        ids.OverrideCount.Should().Be(2);
    }

    [Fact]
    public void NoMastersMeansNoOverrides()
    {
        PluginRecordIds.ResolveNamed(Ids("a", "b"), []).OverrideCount.Should().Be(0);
    }

    [Fact]
    public void ARecordFoundInTwoMastersCountsOnce()
    {
        PluginRecordIds.ResolveNamed(Ids("a"), [Ids("a"), Ids("a")]).OverrideCount.Should().Be(1);
    }

    [Fact]
    public void TwoPluginsOverlapWhenTheyShareARecordIdEvenIfNeitherHasAMaster()
    {
        var a = PluginRecordIds.ResolveNamed(Ids("a", "shared"), []);
        var b = PluginRecordIds.ResolveNamed(Ids("shared", "z"), []);
        var c = PluginRecordIds.ResolveNamed(Ids("other"), []);

        a.Overlaps(b).Should().BeTrue();
        b.Overlaps(a).Should().BeTrue();
        a.Overlaps(c).Should().BeFalse();
    }
}
