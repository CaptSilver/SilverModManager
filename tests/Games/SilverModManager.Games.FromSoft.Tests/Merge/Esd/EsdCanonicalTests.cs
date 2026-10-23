using FluentAssertions;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Merge;
using static SilverModManager.Games.FromSoft.Merge.EsdCanonical;
using static SilverModManager.Games.FromSoft.Tests.Merge.EsdMergeFixture;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

public class EsdCanonicalTests
{
    private static State Zero(params Condition[] conditions) => StateOf(0, conditions);

    [Fact]
    public void ReadsBothSpellingsOfALiteralTheSameWay()
    {
        // FromSoft's compiler has a one-byte push for small ints; both mod toolchains always write the four-byte one.
        Canonical(Zero(Cond(5, Always))).Should().Be(Canonical(Zero(Cond(5, AlwaysInt32))));
    }

    [Fact]
    public void WidensAFloatWithoutChangingIt()
    {
        // One vanilla group writes 1.5 as a float32; both mods promote it to a float64.
        byte[] asFloat32 = [0x82, 0x67, 0x00, 0x00, 0x00, 0x84, 0x80, 0x00, 0x00, 0xC0, 0x3F, 0x94, 0xA1];
        byte[] asFloat64 = [0x82, 0x67, 0x00, 0x00, 0x00, 0x84, 0x81, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF8, 0x3F, 0x94, 0xA1];
        Canonical(Zero(Cond(5, asFloat32))).Should().Be(Canonical(Zero(Cond(5, asFloat64))));
    }

    [Fact]
    public void IgnoresTheMarkersVanillaWritesBetweenOperands()
    {
        // 0xA6 leaves the value stack alone, so it cannot be part of what a condition means.
        byte[] withMarker = [0x4F, 0xB8, 0xA6, 0x40, 0x95, 0xA1];
        byte[] without = [0x4F, 0xB8, 0x40, 0x95, 0xA1];
        Canonical(Zero(Cond(5, withMarker))).Should().Be(Canonical(Zero(Cond(5, without))));
    }

    [Fact]
    public void ExpandsACachedSubexpression()
    {
        // Vanilla computes a subexpression once, parks it in a register (0xA7+n) and reloads it (0xAF+n) in later
        // conditions of the same state; the mods spell it out every time.
        var cached = Zero(Cond(1, [0x4F, 0xB8, 0xA7, 0x40, 0x95, 0xA1]), // b8(15) -> slot 0
                          Cond(2, [0xAF, 0x42, 0x95, 0xA1]));             // slot 0 == 2
        var spelled = Zero(Cond(1, [0x4F, 0xB8, 0x40, 0x95, 0xA1]),
                           Cond(2, [0x4F, 0xB8, 0x42, 0x95, 0xA1]));
        Canonical(cached).Should().Be(Canonical(spelled));
    }

    [Fact]
    public void ReducesXEqualsOneToX()
    {
        Canonical(Zero(Cond(5, [0x4F, 0xB8, 0x82, 0x01, 0x00, 0x00, 0x00, 0x95, 0xA1])))
            .Should().Be(Canonical(Zero(Cond(5, Other))));
    }

    [Fact]
    public void IgnoresWhichSideOfAComparisonHoldsTheConstant()
    {
        // Vanilla writes `0 == f(...)` where Melina writes `f(...) == 0`.
        byte[] literalFirst = [0x40, 0x4F, 0xB8, 0x95, 0xA1];
        byte[] literalLast = [0x4F, 0xB8, 0x40, 0x95, 0xA1];
        Canonical(Zero(Cond(5, literalFirst))).Should().Be(Canonical(Zero(Cond(5, literalLast))));
    }

    [Fact]
    public void CollapsesATrivialWrapper()
    {
        // `IF E: { IF 1 -> T }` and `IF E -> T` are the same machine; different writers use different forms.
        var nested = Zero(Cond(null, Guard, subs: [Cond(5, Always)]));
        var flat = Zero(Cond(5, Guard));
        Canonical(nested).Should().Be(Canonical(flat));
    }

    [Fact]
    public void FlattensAGuardedWrapperIntoAConjunction()
    {
        var nested = Zero(Cond(null, Guard, subs: [Cond(5, Other)]));
        var flat = Zero(Cond(5, GuardAndOther));
        Canonical(nested).Should().Be(Canonical(flat));
    }

    [Fact]
    public void FlattensAWholeChainOfWrappers()
    {
        var deep = Zero(Cond(null, Guard, subs: [Cond(null, Other, subs: [Cond(5, Always)])]));
        var flat = Zero(Cond(5, GuardAndOther));
        Canonical(deep).Should().Be(Canonical(flat));
    }

    [Fact]
    public void WillNotFlattenAWrapperThatRunsCommands()
    {
        // Pass commands on the outer condition fire when E holds and F does not, which the flattened form never
        // does. Collapsing it would lose the commands and call a real edit no change.
        var call = new CommandCall(1, 2);
        var withCall = Zero(Cond(null, Guard, subs: [Cond(5, Other)], passes: [call]));
        var without = Zero(Cond(null, Guard, subs: [Cond(5, Other)]));
        Canonical(withCall).Should().NotBe(Canonical(without));
    }

    [Fact]
    public void WillNotFlattenAwayABranchingSubtree()
    {
        var branching = Zero(Cond(null, Guard, subs: [Cond(null, Other, subs: [Cond(5), Cond(6, Other)])]));
        var empty = Zero(Cond(null, Guard, subs: [Cond(null, Other)]));
        Canonical(branching).Should().NotBe(Canonical(empty));
    }

    [Fact]
    public void IgnoresHowADisjunctionWasGrouped()
    {
        // `||` is associative, and a run of same-target branches merges into one of these, so where the
        // parentheses went is this module's own choice on one side.
        byte[] leftGrouped = [0xB9, 0xBA, 0x96, 0x4F, 0xB8, 0x99, 0x41, 0x99, 0xA1];  // (A || B) || C
        byte[] rightGrouped = [0xB9, 0xBA, 0x96, 0x4F, 0xB8, 0x41, 0x99, 0x99, 0xA1]; // A || (B || C)
        Canonical(Zero(Cond(5, leftGrouped))).Should().Be(Canonical(Zero(Cond(5, rightGrouped))));
    }

    [Fact]
    public void MergesSameTargetSiblings()
    {
        // Two adjacent branches to the same state with nothing else attached are one branch.
        var two = Zero(Cond(5, Guard), Cond(5, Other));
        var one = Zero(Cond(5, GuardOrOther));
        Canonical(two).Should().Be(Canonical(one));
    }

    [Fact]
    public void MergesADuplicatedBranchIntoASelfDisjunction()
    {
        // Vanilla has the identical condition twice and Melina writes `E || E`. It does not simplify that back
        // down to `E`, so neither does this.
        var twice = Zero(Cond(5, Guard), Cond(5, Guard));
        var merged = Zero(Cond(5, GuardOrGuard));
        Canonical(twice).Should().Be(Canonical(merged));
        Canonical(twice).Should().NotBe(Canonical(Zero(Cond(5, Guard))));
    }

    [Fact]
    public void WillNotMergeSiblingsThatJumpSomewhereElse()
    {
        Canonical(Zero(Cond(5, Guard), Cond(6, Other))).Should().NotBe(Canonical(Zero(Cond(5, GuardOrOther))));
    }

    [Fact]
    public void ReadsACommandArgument()
    {
        // An ESD talk command is `ShowShopMessage(3)`, not `ShowShopMessage`; dropping the arguments makes two
        // different calls compare equal.
        Canonical(Calling([0x42, 0xA1])).Should().NotBe(Canonical(Calling([0x43, 0xA1])));
    }

    [Fact]
    public void ReadsAnArgumentThroughTheSameDecoder()
    {
        // The re-spellings show up in argument bytecode too, so comparing raw argument bytes would report an edit
        // wherever a mod tool rewrote a call it never touched.
        Canonical(Calling(Always)).Should().Be(Canonical(Calling(AlwaysInt32)));
    }

    [Fact]
    public void KeepsARealDifference()
    {
        Canonical(Zero(Cond(5))).Should().NotBe(Canonical(Zero(Cond(6))));
        Canonical(Zero(Cond(5, [0x4F, 0xB8, 0x42, 0x95, 0xA1]))).Should()
            .NotBe(Canonical(Zero(Cond(5, [0x4F, 0xB8, 0x43, 0x95, 0xA1]))));
    }

    [Fact]
    public void WillNotFoldSameTargetBranchesWithNoEvaluator()
    {
        // A condition with no evaluator has no expression to disjoin; folding two of them would build an
        // operation over nothing.
        var canonical = Canonical(Zero(Cond(5, []), Cond(5, [])));
        canonical.Conditions.Should().HaveCount(2);
        canonical.Conditions.Select(c => c.Evaluator).Should().AllBeEquivalentTo((EsdExpr?)null);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xA1 })] // an opcode the decoder has never seen
    [InlineData(new byte[] { 0x95, 0xA1 })] // underflows the stack
    [InlineData(new byte[] { 0xAF, 0xA1 })] // reads a slot nothing wrote
    [InlineData(new byte[] { 0x41, 0x41, 0xA1 })] // two values left
    [InlineData(new byte[] { 0x41 })] // no terminator
    [InlineData(new byte[] { 0x82, 0x01, 0x00 })] // cut off mid-literal
    public void RefusesBytecodeItCannotDecode(byte[] bytecode)
    {
        // An opcode the decoder has never seen means the canonical form is a guess; say so instead of quietly
        // reporting two machines as different.
        var act = () => Canonical(Zero(Cond(5, bytecode)));
        act.Should().Throw<EsdMergeException>();
    }
}
