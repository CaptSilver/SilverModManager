using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

public class MergeStrategiesTests
{
    private static readonly MergeStrategy[] All = Enum.GetValues<MergeStrategy>();

    // A game without a regulation takes the name from the test, not from a constant the code under test could share.
    public static FromSoftFormatTraits WithRegulation(string name) => TestSoulsGame.Definition.Formats with { RegulationFileName = name };

    [Fact]
    public void Every_strategy_has_a_unique_erm_spelled_name()
    {
        All.Select(MergeStrategies.Name).Should().OnlyHaveUniqueItems();
        All.ToDictionary(s => s, MergeStrategies.Name).Should().BeEquivalentTo(new Dictionary<MergeStrategy, string>
        {
            [MergeStrategy.FmgUnion] = "fmg-union",
            [MergeStrategy.FmgThreeWay] = "fmg-3way",
            [MergeStrategy.ParamRows] = "param-rows",
            [MergeStrategy.TpfUnion] = "tpf-union",
            [MergeStrategy.EsdThreeWay] = "esd-3way",
        });
    }

    [Fact]
    public void NeedsVanilla_is_the_strategies_that_read_the_file_the_mods_branched_from()
    {
        All.Where(MergeStrategies.NeedsVanilla).Should().BeEquivalentTo([MergeStrategy.FmgThreeWay, MergeStrategy.ParamRows, MergeStrategy.EsdThreeWay]);
    }

    [Fact]
    public void TakesGameBase_is_param_rows_only()
    {
        All.Where(MergeStrategies.TakesGameBase).Should().BeEquivalentTo([MergeStrategy.ParamRows]);
    }

    [Fact]
    public void Every_strategy_produces_notes()
    {
        All.Should().OnlyContain(s => MergeStrategies.ProducesNotes(s));
    }

    [Theory]
    [InlineData("regulation.bin", false, MergeStrategy.ParamRows)]
    [InlineData("REGULATION.BIN", false, MergeStrategy.ParamRows)]
    [InlineData("sub/regulation.bin", false, null)]
    [InlineData("msg/engus/item.msgbnd.dcx", false, MergeStrategy.FmgUnion)]
    [InlineData("msg/engus/item.msgbnd.dcx", true, MergeStrategy.FmgThreeWay)]
    [InlineData("MSG/ENGUS/ITEM.MSGBND.DCX", true, MergeStrategy.FmgThreeWay)]
    [InlineData("menu/hi/00_solo.tpf.dcx", false, MergeStrategy.TpfUnion)]
    [InlineData("menu/hi/00_solo.tpf.dcx", true, MergeStrategy.TpfUnion)]
    [InlineData("script/talk/m00_00_00_00.talkesdbnd.dcx", false, MergeStrategy.EsdThreeWay)]
    [InlineData("map/m10.mapbnd.dcx", false, null)]
    [InlineData("sfx/effect.ffxbnd.dcx", true, null)]
    [InlineData("readme.txt", false, null)]
    public void ForPath_names_the_strategy_from_the_file_name(string path, bool vanilla, MergeStrategy? expected)
    {
        MergeStrategies.ForPath(path, WithRegulation("regulation.bin"), vanilla).Should().Be(expected);
    }

    [Fact]
    public void ForPath_reads_the_regulation_name_from_the_traits()
    {
        MergeStrategies.ForPath("regulation.dat", WithRegulation("regulation.dat"), false).Should().Be(MergeStrategy.ParamRows);
        MergeStrategies.ForPath("regulation.bin", WithRegulation("regulation.dat"), false).Should().BeNull();
    }

    [Fact]
    public void ForPath_finds_no_regulation_when_the_game_has_none()
    {
        MergeStrategies.ForPath("regulation.bin", TestSoulsGame.Definition.Formats, false).Should().BeNull();
    }

    [Fact]
    public void ForPath_takes_the_regulation_from_elden_rings_traits_too()
    {
        var traits = EldenRingGame.Definition.Formats;
        MergeStrategies.ForPath(traits.RegulationFileName!, traits, false).Should().Be(MergeStrategy.ParamRows);
    }
}
