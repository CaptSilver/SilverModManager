using FluentAssertions;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Heal;
using NexusMods.Games.FromSoft.Tests.Formats;

namespace NexusMods.Games.FromSoft.Tests.Heal;

public class LayoutGateTests
{
    private static FromSoftFormatTraits T => EldenRingGame.Definition.Formats;

    private static IReadOnlyDictionary<string, Layout?> Layouts(params (string Name, Layout? Layout)[] tables)
        => tables.ToDictionary(t => t.Name, t => t.Layout);

    private static IReadOnlyList<string> Gate(IReadOnlyDictionary<string, Layout?> baseline, params (string Mod, IReadOnlyDictionary<string, Layout?> Layouts)[] mods)
        => LayoutGate.Check(baseline, mods);

    [Fact]
    public void PassesWhenEveryStrideMatches()
    {
        var layouts = Layouts(("EquipParamWeapon.param", new Layout(200, 1024, 3)), ("SpEffectParam.param", new Layout(90, 512, 2)));

        Gate(layouts, ("clevers", layouts)).Should().BeEmpty();
    }

    [Fact]
    public void NamesTheTableAndModWhenAStrideMoves()
    {
        var problems = Gate(
            Layouts(("EquipParamWeapon.param", new Layout(200, 1032, 3))),
            ("clevers", Layouts(("EquipParamWeapon.param", new Layout(200, 1024, 3)))));

        problems.Should().ContainSingle().Which.Should().Contain("clevers").And.Contain("EquipParamWeapon.param").And.Contain("1024").And.Contain("1032");
    }

    [Fact]
    public void CatchesAMovedParamdefVersion()
    {
        var problems = Gate(
            Layouts(("SpEffectParam.param", new Layout(90, 512, 4))),
            ("nofalldead", Layouts(("SpEffectParam.param", new Layout(90, 512, 2)))));

        problems.Should().ContainSingle().Which.Should().Contain("paramdef");
    }

    [Fact]
    public void ATableTheBaselineLacksIsNotALayoutProblem()
    {
        // A mod may ship a table the baseline does not carry. That is a merge question, not a transplant-safety one.
        Gate(Layouts(), ("weird", Layouts(("Odd.param", new Layout(4, 8, 1))))).Should().BeEmpty();
    }

    [Fact]
    public void ReportsEveryModNotJustTheFirst()
    {
        var baseline = Layouts(("A.param", new Layout(7, 10, 1)));
        var moved = Layouts(("A.param", new Layout(7, 12, 1)));

        Gate(baseline, ("a", moved), ("b", moved)).Should().HaveCount(2);
    }

    [Fact]
    public void SkipsATableTheBaselineCannotRead()
    {
        Gate(Layouts(("Cutscene.param", null)), ("clevers", Layouts(("Cutscene.param", new Layout(107, 16, 1))))).Should().BeEmpty();
    }

    [Fact]
    public void SkipsATableTheModCannotRead()
    {
        Gate(Layouts(("Cutscene.param", new Layout(107, 16, 1))), ("clevers", Layouts(("Cutscene.param", null)))).Should().BeEmpty();
    }

    [Fact]
    public void IgnoresAPaddingOnlyStrideOnASingleRowParam()
    {
        // A one-row param has no gap between rows, so its stride is the row plus whatever alignment the writer left:
        // 16 in the game's own file, 8 in a re-saved mod copy. Nothing moved.
        Gate(Layouts(("PlayerCommonParam.param", new Layout(1, 264, 1))), ("clevers", Layouts(("PlayerCommonParam.param", new Layout(1, 256, 1))))).Should().BeEmpty();
    }

    [Fact]
    public void StillComparesTheParamdefVersionOnASingleRowParam()
    {
        var problems = Gate(
            Layouts(("PlayerCommonParam.param", new Layout(1, 264, 2))),
            ("clevers", Layouts(("PlayerCommonParam.param", new Layout(1, 256, 1)))));

        problems.Should().ContainSingle().Which.Should().Contain("paramdef");
    }

    [Fact]
    public void ReportsAStrideThatMovedOnAMultiRowParam()
    {
        var problems = Gate(
            Layouts(("SpEffectParam.param", new Layout(2, 264, 1))),
            ("clevers", Layouts(("SpEffectParam.param", new Layout(2, 256, 1)))));

        problems.Should().ContainSingle().Which.Should().Contain("row stride");
    }

    [Fact]
    public void ABaselineWithOneRowAgainstAModWithManyIsNotStrideComparable()
    {
        Gate(Layouts(("A.param", new Layout(1, 16, 1))), ("m", Layouts(("A.param", new Layout(9, 8, 1))))).Should().BeEmpty();
    }

    [Fact]
    public void GivesTheSameProblemStringsAsErmOverGoldenRegulations()
    {
        var baseline = GoldenFilesTests.Read("layout_gate_baseline.bin");
        var mods = new (string, byte[])[]
        {
            ("alpha", GoldenFilesTests.Read("layout_gate_mod_stride.bin")),
            ("beta", GoldenFilesTests.Read("layout_gate_mod_paramdef.bin")),
        };

        var problems = LayoutGate.Check(baseline, mods, T);

        problems.Should().Equal(System.Text.Encoding.UTF8.GetString(GoldenFilesTests.Read("layout_gate_problems.txt")).Split('\n'));
    }

    [Fact]
    public void AModRegulationIsDecryptedOncePerFile()
    {
        var baseline = GoldenFilesTests.Read("layout_gate_baseline.bin");
        var mod = GoldenFilesTests.Read("layout_gate_mod_stride.bin");
        var seen = new List<string>();

        LayoutGate.Check(baseline, [("alpha", mod)], T, (blob, traits) =>
        {
            seen.Add(Convert.ToBase64String(blob));
            return Regulation.Unpack(blob, traits);
        });

        seen.Should().OnlyHaveUniqueItems().And.HaveCount(2);
    }
}
