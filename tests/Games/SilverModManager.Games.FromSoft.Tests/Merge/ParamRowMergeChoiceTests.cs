using FluentAssertions;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

/// <summary>A pin on a contested row decides it; a row the game's own copy beat is not between two mods and ignores pins.</summary>
public class ParamRowMergeChoiceTests
{
    private const string Type = "SP_EFFECT_PARAM_ST";
    private const string Base = "Mod A";
    private const string Next = "Mod B";
    private const int Row = 100;
    private const int Invented = 80020000;

    private static FromSoftFormatTraits T => EldenRingGame.Definition.Formats;

    private static byte[] Rep(int value) => Enumerable.Repeat((byte)value, 8).ToArray();

    private static byte[] Reg(params (int Id, byte[] Data)[] rows)
    {
        var sorted = rows.OrderBy(r => r.Id).ToArray();
        var next = 0;
        var blob = ParamFixture.Make(sorted.Select(r => r.Id).ToArray(), 8, Type, fill: _ => sorted[next++].Data);
        return RegulationFixture.BuildFromEntries([(1, $"{Type}.param", blob)], "07D7R6\0\0", T);
    }

    private static byte[] RowOf(byte[] regulation, int id)
        => Param.Read(Regulation.Entries(regulation, T).Single().Data).Rows.Single(r => r.Id == id).Data;

    private static PathChoices Pin(string key, string winner) => new([(key, winner)]);

    [Fact]
    public void ChosenNext_TakesTheNextRow()
    {
        var choices = Pin($"param:1/{Row}", Next);
        var notes = new List<IMergeNote>();

        var merged = ParamRowMerge.Merge(Reg((Row, Rep(0xaa))), Reg((Row, Rep(0xbb))), Reg((Row, Rep(0x00))), T, notes, false, Base, Next, choices: choices);

        RowOf(merged, Row).Should().Equal(Rep(0xbb));
        choices.Owner($"param:1/{Row}").Should().Be(Next);
        // The note is still written by priority; the planner relabels it once the fold is done.
        notes.Should().Equal(new RowConflict(1, Row, Base, Next));
    }

    [Fact]
    public void ChosenBase_KeepsTheBase()
    {
        var choices = Pin($"param:1/{Row}", Base);

        var merged = ParamRowMerge.Merge(Reg((Row, Rep(0xaa))), Reg((Row, Rep(0xbb))), Reg((Row, Rep(0x00))), T, null, false, Base, Next, choices: choices);

        RowOf(merged, Row).Should().Equal(Rep(0xaa));
        choices.Owner($"param:1/{Row}").Should().BeNull();
    }

    [Fact]
    public void GameRow_IsNotChoosable()
    {
        var choices = Pin($"param:1/{Row}", Next);
        var notes = new List<IMergeNote>();

        var merged = ParamRowMerge.Merge(Reg((Row, Rep(0xaa))), Reg((Row, Rep(0xbb))), Reg((Row, Rep(0x00))), T, notes, true, null, Next, choices: choices);

        RowOf(merged, Row).Should().Equal(Rep(0xaa), "the game's row beat the mod's and no pin can hand it to a mod");
        choices.Owner($"param:1/{Row}").Should().BeNull();
        notes.Should().ContainSingle().Which.Should().BeOfType<RowConflict>().Which.KeptIsGame.Should().BeTrue();
    }

    [Fact]
    public void RivalRow_IsChoosable()
    {
        var choices = Pin($"param:1/{Invented}", Next);
        var vanilla = Reg((1, Rep(0xab)));

        var merged = ParamRowMerge.Merge(
            Reg((1, Rep(0xab)), (Invented, Rep(0x11))), Reg((1, Rep(0xab)), (Invented, Rep(0x22))), vanilla, T, null, false, Base, Next, choices: choices);

        RowOf(merged, Invented).Should().Equal(Rep(0x22));
        choices.Owner($"param:1/{Invented}").Should().Be(Next);
    }
}
