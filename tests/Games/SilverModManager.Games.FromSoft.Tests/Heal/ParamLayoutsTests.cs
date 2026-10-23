using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests.Heal;

public class ParamLayoutsTests
{
    private static FromSoftFormatTraits T => EldenRingGame.Definition.Formats;

    [Fact]
    public void AnUnreadableTableIsRecordedAsNull()
    {
        // FromSoft ships a few params whose strings offset points past the end of the file. The gate asks whether a
        // transplant is safe, and a table nobody transplants never has to be read.
        var layouts = ParamLayouts.Of([new BinderEntry(1, "GR\\Cutscene.param", "junk"u8.ToArray())]);

        layouts.Should().ContainKey("Cutscene.param").WhoseValue.Should().BeNull();
    }

    [Fact]
    public void TheRowCountStrideAndParamdefVersionAreRecorded()
    {
        var blob = ParamFixture.Make([1], stride: 64, paramType: "PLAYER_COMMON_ST");

        var layouts = ParamLayouts.Of([new BinderEntry(1, "GR\\PlayerCommonParam.param", blob)]);

        layouts["PlayerCommonParam.param"].Should().Be(new Layout(1, 64, 4));
    }

    [Fact]
    public void TablesAreNamedAfterTheLastBackslashAndOnlyParamEntriesAreRead()
    {
        var blob = ParamFixture.Make([1, 2, 3], stride: 8);

        var layouts = ParamLayouts.Of([
            new BinderEntry(1, "GR\\param\\gameparam\\SpEffectParam.param", blob),
            new BinderEntry(2, "SHOUTING.PARAM", blob),
            new BinderEntry(3, "GR\\param\\gameparam\\notes.txt", "junk"u8.ToArray()),
        ]);

        layouts.Keys.Should().Equal("SpEffectParam.param", "SHOUTING.PARAM");
        layouts["SpEffectParam.param"].Should().Be(new Layout(3, 8, 4));
    }

    [Fact]
    public void ARegulationPayloadIsReadThroughTheGamesBinder()
    {
        var payload = RegulationFixture.Payload([(1, "GR\\A.param", ParamFixture.Make([1, 2], stride: 16))], "11701000");

        ParamLayouts.Of(payload, T)["A.param"].Should().Be(new Layout(2, 16, 4));
    }
}
