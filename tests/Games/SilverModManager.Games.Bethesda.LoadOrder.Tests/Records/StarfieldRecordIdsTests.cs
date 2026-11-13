using FluentAssertions;

namespace SilverModManager.Games.Bethesda.LoadOrder.Tests.Records;

// Follows the Starfield cases of esplugin's hashed_masters_for_starfield and hashed_parent (plugin.rs).
public class StarfieldRecordIdsTests
{
    private static PluginRecordIds Resolve(string name, PluginScale scale, (string Name, PluginScale Scale)[] masters, params uint[] formIds)
        => PluginRecordIds.ResolveStarfield(name, scale, masters, formIds);

    private const PluginScale Full = PluginScale.Full;
    private const PluginScale Medium = PluginScale.Medium;
    private const PluginScale Small = PluginScale.Small;

    [Fact]
    public void FullMastersAreNumberedFromZeroInHeaderOrder()
    {
        var ids = Resolve("A.esp", Full, [("Starfield.esm", Full), ("Other.esm", Full)], 0x0000_0800, 0x0100_0801, 0x0200_0802);

        ids.OverrideCount.Should().Be(2);
    }

    [Fact]
    public void MediumMastersAreNumberedFromFdSeparatelyFromFullOnes()
    {
        // The medium master is second in the header, but the first of the medium numbering, so 0xFD000000 and not 0x01.
        var a = Resolve("A.esp", Full, [("Starfield.esm", Full), ("Med.esp", Medium)], 0xFD00_0123);
        var b = Resolve("B.esp", Full, [("Med.esp", Medium)], 0xFD00_0123);
        var notMedium = Resolve("C.esp", Full, [("Starfield.esm", Full), ("Med.esp", Medium)], 0x0100_0123);

        a.OverrideCount.Should().Be(1);
        a.Overlaps(b).Should().BeTrue();
        notMedium.OverrideCount.Should().Be(0, "index 1 is past the one full master, so it names the plugin itself");
        notMedium.Overlaps(b).Should().BeFalse();
    }

    [Fact]
    public void AMediumMastersRecordsAreMatchedOnTheirLowTwoBytes()
    {
        var a = Resolve("A.esp", Full, [("Med.esp", Medium)], 0xFD00_1234);
        var b = Resolve("B.esp", Full, [("Med.esp", Medium)], 0xFD00_1234);
        var differentLowBytes = Resolve("C.esp", Full, [("Med.esp", Medium)], 0xFD00_1235);

        a.Overlaps(b).Should().BeTrue();
        a.Overlaps(differentLowBytes).Should().BeFalse();
    }

    [Fact]
    public void SmallMastersAreNumberedFromFeInSteps()
    {
        var first = Resolve("A.esp", Full, [("S1.esl", Small), ("S2.esl", Small)], 0xFE00_0123);
        var second = Resolve("B.esp", Full, [("S1.esl", Small), ("S2.esl", Small)], 0xFE00_1123);
        var againstFirst = Resolve("C.esp", Full, [("S1.esl", Small)], 0xFE00_0123);
        var againstSecond = Resolve("D.esp", Full, [("S2.esl", Small)], 0xFE00_0123);

        first.OverrideCount.Should().Be(1);
        second.OverrideCount.Should().Be(1);
        first.Overlaps(againstFirst).Should().BeTrue();
        first.Overlaps(againstSecond).Should().BeFalse();
        second.Overlaps(againstSecond).Should().BeTrue();
        second.Overlaps(againstFirst).Should().BeFalse();
    }

    [Fact]
    public void EachScaleIsNumberedOnItsOwn()
    {
        var masters = new[] { ("F.esm", Full), ("M1.esp", Medium), ("S1.esl", Small), ("M2.esp", Medium), ("F2.esm", Full) };
        // F.esm 0x00, F2.esm 0x01, M1 0xFD00, M2 0xFD01, S1 0xFE000.
        var ids = Resolve("A.esp", Full, masters, 0x0100_0800, 0xFD01_0800, 0xFE00_0800, 0x0200_0800);

        ids.OverrideCount.Should().Be(3, "index 2 is past the two full masters");
        ids.Overlaps(Resolve("B.esp", Full, [("M2.esp", Medium)], 0xFD00_0800)).Should().BeTrue();
        ids.Overlaps(Resolve("C.esp", Full, [("F2.esm", Full)], 0x0000_0800)).Should().BeTrue();
    }

    [Fact]
    public void ANewRecordOfAMediumPluginIsKeyedByItsScaleWhateverModIndexItWasWrittenWith()
    {
        // The game replaces the mod index with the plugin's own at load, so the plugin's scale is what counts.
        var written = Resolve("Med.esp", Medium, [], 0x0500_3456);
        var overridden = Resolve("B.esp", Full, [("Med.esp", Medium)], 0xFD00_3456);

        written.OverrideCount.Should().Be(0);
        written.Overlaps(overridden).Should().BeTrue();
    }

    [Fact]
    public void ANewRecordOfAFullPluginKeepsAllThreeLowBytes()
    {
        var a = Resolve("A.esp", Full, [], 0x0112_3456);
        var sameLowTwoBytes = Resolve("B.esp", Full, [("A.esp", Full)], 0x0000_3456);
        var sameLowThreeBytes = Resolve("C.esp", Full, [("A.esp", Full)], 0x0012_3456);

        a.Overlaps(sameLowTwoBytes).Should().BeFalse();
        a.Overlaps(sameLowThreeBytes).Should().BeTrue();
    }

    [Fact]
    public void ANewRecordOfASmallPluginKeepsTheLowTwelveBits()
    {
        var a = Resolve("A.esl", Small, [], 0x0000_0123, 0x0000_1124);
        var b = Resolve("B.esp", Full, [("A.esl", Small)], 0xFE00_0123);

        a.Overlaps(b).Should().BeTrue();
        // 0x1124 masks down to 0x124, so a record of another plugin naming 0x124 of A is the same one.
        a.Overlaps(Resolve("C.esp", Full, [("A.esl", Small)], 0xFE00_0124)).Should().BeTrue();
        a.Overlaps(Resolve("D.esp", Full, [("A.esl", Small)], 0xFE00_0125)).Should().BeFalse();
    }

    [Fact]
    public void MasterNamesAreComparedIgnoringCase()
    {
        var a = Resolve("A.esp", Full, [("STARFIELD.ESM", Full)], 0x0000_0800);
        var b = Resolve("B.esp", Full, [("starfield.esm", Full)], 0x0000_0800);

        a.Overlaps(b).Should().BeTrue();
    }
}
