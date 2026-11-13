using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

public class FmgMergeChoiceTests
{
    private const string Base = "Mod A";
    private const string Next = "Mod B";

    private static FromSoftFormatTraits T => EldenRingGame.Definition.Formats;

    private static byte[] Msgbnd(string text)
        => MsgbndFixture.Build(new Dictionary<int, IReadOnlyDictionary<int, string?>> { [1] = new Dictionary<int, string?> { [10] = text } }, T);

    private static string TextOf(byte[] archive) => MsgbndFixture.ReadBack(archive, T)[1][10]!;

    [Fact]
    public void ChosenNext_TakesTheNextText()
    {
        var choices = new PathChoices([("fmg:1/10", Next)]);

        var merged = FmgMerge.Union(Msgbnd("a"), Msgbnd("b"), T, null, Base, Next, choices);

        TextOf(merged).Should().Be("b");
        choices.Owner("fmg:1/10").Should().Be(Next);
    }

    [Fact]
    public void ChosenBase_KeepsTheBaseText()
    {
        var choices = new PathChoices([("fmg:1/10", Base)]);

        TextOf(FmgMerge.Union(Msgbnd("a"), Msgbnd("b"), T, null, Base, Next, choices)).Should().Be("a");
        choices.Owner("fmg:1/10").Should().BeNull();
    }

    [Fact]
    public void ChosenNext_TakesTheNextTextOfAThreeWayConflict()
    {
        var choices = new PathChoices([("fmg:1/10", Next)]);

        var merged = FmgMerge.ThreeWay(Msgbnd("a"), Msgbnd("b"), Msgbnd("vanilla"), T, null, Base, Next, choices);

        TextOf(merged).Should().Be("b");
        choices.Owner("fmg:1/10").Should().Be(Next);
    }
}
