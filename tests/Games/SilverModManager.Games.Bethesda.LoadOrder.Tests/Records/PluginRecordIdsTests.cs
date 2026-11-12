using FluentAssertions;

namespace SilverModManager.Games.Bethesda.LoadOrder.Tests.Records;

// Follows the resolved_record_id and Plugin override and overlap tests in esplugin (record_id.rs, plugin.rs).
public class PluginRecordIdsTests
{
    private static PluginRecordIds Resolve(string name, string[] masters, params uint[] formIds)
        => PluginRecordIds.Resolve(name, masters, formIds);

    [Fact]
    public void CountsARecordWhoseModIndexNamesAMasterAsAnOverride()
    {
        var ids = Resolve("Mod.esp", ["Skyrim.esm", "Update.esm"], 0x0000_0D62, 0x0100_0D63, 0x0200_0800);

        ids.OverrideCount.Should().Be(2);
        ids.Count.Should().Be(3);
    }

    [Fact]
    public void DoesNotCountARecordWhoseModIndexIsPastTheMasters()
    {
        // Index 2 is the plugin itself when it has two masters, and anything above that names no plugin at all.
        Resolve("Mod.esp", ["Skyrim.esm", "Update.esm"], 0x0200_0800, 0x0500_0801).OverrideCount.Should().Be(0);
    }

    [Fact]
    public void IgnoresMastersPastTheFirst256()
    {
        var masters = Enumerable.Range(0, 257).Select(i => $"M{i}.esm").ToArray();

        // Index 0x00 is the first master; a 257th master has no byte to be named by.
        Resolve("Mod.esp", masters, 0x0000_0800).OverrideCount.Should().Be(1);
        Resolve("Mod.esp", masters, 0xFF00_0800).OverrideCount.Should().Be(1);
    }

    [Fact]
    public void NoRecordsMeansNoOverrides()
    {
        var ids = Resolve("Mod.esp", ["Skyrim.esm"]);

        ids.OverrideCount.Should().Be(0);
        ids.Count.Should().Be(0);
        ids.Overlaps(Resolve("Other.esp", ["Skyrim.esm"], 0x0000_0800)).Should().BeFalse();
    }

    [Fact]
    public void TwoPluginsOverlapWhenTheyOverrideTheSameMasterRecord()
    {
        var a = Resolve("A.esp", ["Skyrim.esm"], 0x0000_0D62, 0x0000_0D70);
        var b = Resolve("B.esp", ["Skyrim.esm"], 0x0000_0D62);

        a.Overlaps(b).Should().BeTrue();
        b.Overlaps(a).Should().BeTrue();
    }

    [Fact]
    public void TheMasterIndexIsPerPlugin_SoTheSameRecordIsMatchedAcrossDifferentMasterLists()
    {
        // 0x01000D62 is Update.esm's record in A and Skyrim.esm's record in B only when the lists differ.
        var a = Resolve("A.esp", ["Skyrim.esm", "Update.esm"], 0x0100_0D62);
        var b = Resolve("B.esp", ["Update.esm"], 0x0000_0D62);

        a.Overlaps(b).Should().BeTrue();
    }

    [Fact]
    public void PluginsDoNotOverlapWhenTheirRecordsComeFromDifferentMasters()
    {
        var a = Resolve("A.esp", ["Skyrim.esm"], 0x0000_0D62);
        var b = Resolve("B.esp", ["Update.esm"], 0x0000_0D62);

        a.Overlaps(b).Should().BeFalse();
    }

    [Fact]
    public void AnOverrideOfAPluginsNewRecordOverlapsThatPlugin()
    {
        // A's own record carries mod index 1, since A comes after its one master. B lists A as its second master, so
        // the same FormID in B is an override of A's record.
        var a = Resolve("A.esp", ["Skyrim.esm"], 0x0100_0800);
        var b = Resolve("B.esp", ["Skyrim.esm", "A.esp"], 0x0100_0800);

        a.OverrideCount.Should().Be(0);
        b.OverrideCount.Should().Be(1);
        a.Overlaps(b).Should().BeTrue();
    }

    [Fact]
    public void ARecordBPluginAddsItselfDoesNotOverlapAnotherPluginsRecordWithTheSameObjectIndex()
    {
        var a = Resolve("A.esp", ["Skyrim.esm"], 0x0100_0800);
        var b = Resolve("B.esp", ["Skyrim.esm", "A.esp"], 0x0200_0800);

        b.OverrideCount.Should().Be(0);
        a.Overlaps(b).Should().BeFalse();
    }

    [Fact]
    public void MasterNamesAreComparedIgnoringCase()
    {
        var a = Resolve("A.esp", ["SKYRIM.ESM"], 0x0000_0D62);
        var b = Resolve("B.esp", ["skyrim.esm"], 0x0000_0D62);

        a.Overlaps(b).Should().BeTrue();
    }

    [Fact]
    public void ARecordAMasterOwnsInsideTheLightRangeIsStillMatchedOnItsLowThreeBytes()
    {
        // Outside Starfield the object index is always the low 24 bits, for light plugins too.
        var a = Resolve("A.esp", ["Skyrim.esm"], 0x0000_0FFF);
        var b = Resolve("B.esp", ["Skyrim.esm"], 0x0000_0FFF);
        var c = Resolve("C.esp", ["Skyrim.esm"], 0x0000_1FFF);

        a.Overlaps(b).Should().BeTrue();
        a.Overlaps(c).Should().BeFalse();
    }
}
