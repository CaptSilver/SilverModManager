using FluentAssertions;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests.Formats;

public class EsdTests
{
    private static IReadOnlyList<(int GroupId, IReadOnlyList<(int StateId, int? Target)> States)> Groups(
        params (int, (int, int?)[])[] groups) =>
        groups.Select(g => (g.Item1, (IReadOnlyList<(int, int?)>)g.Item2)).ToList();

    private static byte[] Synthetic(params (int, (int, int?)[])[] groups) => EsdFixture.Synthetic(Groups(groups));

    private static readonly byte[] GoldenSynthetic = GoldenFilesTests.Read("esd_synthetic.esd");
    private static readonly byte[] GoldenRewritten = GoldenFilesTests.Read("esd_synthetic_rewritten.esd");

    private static EsdFile Archive(params StateGroup[] groups) => new(groups, "", [0u, 0u, 0u, 0u]);

    // ---- goldens

    [Fact]
    public void TheFixtureReproducesTheBytesErmBuiltForTheSameArguments()
    {
        EsdFixture.Synthetic(Groups((1, [(0, 1), (1, null)]), (7, [(0, null)])), extraPoolSlots: 2)
            .Should().Equal(GoldenSynthetic);
    }

    [Fact]
    public void WritingWhatWasReadMatchesWhatErmWrote()
    {
        Esd.Write(Esd.Read(GoldenSynthetic)).Should().Equal(GoldenRewritten);
    }

    [Fact]
    public void TheRewrittenGoldenRoundTripsUnchanged()
    {
        Esd.Write(Esd.Read(GoldenRewritten)).Should().Equal(GoldenRewritten);
    }

    // ---- read

    [Fact]
    public void ReadReturnsGroupsStatesAndTargets()
    {
        var parsed = Esd.Read(Synthetic((1, [(0, 1), (1, null)])));
        parsed.Groups.Select(g => g.Id).Should().Equal(1L);
        parsed.Groups[0].States.Select(s => s.Id).Should().Equal(0L, 1L);
        parsed.Groups[0].States[0].Conditions[0].Target.Should().Be(1);
        parsed.Groups[0].States[1].Conditions.Should().BeEmpty();
        parsed.Name.Should().Be("t000001000");
    }

    [Fact]
    public void ReadDropsTheTrailingDummyState()
    {
        var parsed = Esd.Read(Synthetic((1, [(0, null), (1, null), (2, null)])));
        parsed.Groups[0].States.Select(s => s.Id).Should().Equal(0L, 1L, 2L);
    }

    [Fact]
    public void ReadRejectsABadMagic()
    {
        var raw = Synthetic((1, [(0, null), (1, null)]));
        raw[0] = (byte)'X';
        var act = () => Esd.Read(raw);
        act.Should().Throw<EsdException>().WithMessage("*magic*");
    }

    [Fact]
    public void ReadRejectsADirectoryThatDoesNotTileTheFile()
    {
        var raw = EsdFixture.Corrupt(Synthetic((1, [(0, null), (1, null)])), 0x14, 999999, 4);
        var act = () => Esd.Read(raw);
        act.Should().Throw<EsdException>();
    }

    [Fact]
    public void ReadRejectsATableCountThatOverflowsTheBuffer()
    {
        // The data size and the pool offset are checked against each other, so a crafted header can inflate a
        // count and keep both consistent; only a bound against the real buffer catches it.
        var raw = Synthetic((1, [(0, null), (1, null)]));
        const uint inflated = 999999;
        raw = EsdFixture.Corrupt(raw, 0x28, inflated, 4);
        uint U32(int at) => BitConverter.ToUInt32(raw, at);
        var statesAt = Esd.InternalHeaderSize + (long)inflated * Esd.GroupSize;
        var condsAt = statesAt + U32(0x30) * (long)Esd.StateSize;
        var callsAt = condsAt + U32(0x38) * (long)Esd.ConditionSize;
        var argsAt = callsAt + U32(0x40) * (long)Esd.CommandCallSize;
        var poolAt = argsAt + U32(0x48) * (long)Esd.CommandArgSize;
        raw = EsdFixture.Corrupt(raw, 0x4C, poolAt, 4); // keep the tiling check quiet so only the bounds check can catch this
        var act = () => Esd.Read(raw);
        act.Should().Throw<EsdException>().WithMessage("*runs past the end*");
    }

    [Fact]
    public void ReadRejectsACrossGroupJumpTarget()
    {
        // No shipped file has one, and a merge assumes a group is self-contained for jumps.
        var raw = Synthetic((1, [(0, 1), (1, null)]), (2, [(0, null), (1, null)]));
        var condsAt = Esd.HeaderSize + Esd.InternalHeaderSize + 2 * Esd.GroupSize + 6 * Esd.StateSize;
        raw = EsdFixture.Corrupt(raw, condsAt, Esd.InternalHeaderSize + 2 * Esd.GroupSize + 3 * Esd.StateSize, 8);
        var act = () => Esd.Read(raw);
        act.Should().Throw<EsdException>().WithMessage("*group*");
    }

    // ---- write: round trips

    [Fact]
    public void WriteRoundTripsASyntheticFile()
    {
        var raw = Synthetic((1, [(0, 1), (1, null)]), (2, [(0, null), (1, 0)]));
        Esd.Write(Esd.Read(raw)).Should().Equal(raw);
    }

    [Fact]
    public void WriteEmitsThePoolSizeItUsedNotTheOneTheSourceDeclared()
    {
        // The header has to describe the pool laid out; a source may declare slots its graph never reaches (vanilla
        // does), and carrying that through would promise slots the bytes do not hold.
        const int padding = 128;
        var raw = EsdFixture.Synthetic(Groups((1, [(0, 1), (1, null)])), extraPoolSlots: padding);
        var reached = EsdFixture.DeclaredPoolCount(raw) - padding;

        var rewritten = Esd.Write(Esd.Read(raw));
        EsdFixture.DeclaredPoolCount(rewritten).Should().Be(reached);

        var again = Esd.Read(rewritten);
        again.Groups[0].States.Select(s => s.Id).Should().Equal(0L, 1L);
        again.Groups[0].States[0].Conditions[0].Target.Should().Be(1);
    }

    [Fact]
    public void WriteAppendsANewStateAndKeepsEveryTargetResolvable()
    {
        // Inserting a state shifts the whole state table, so a writer that patched offsets would corrupt every target.
        var parsed = Esd.Read(Synthetic((1, [(0, 1), (1, null)])));
        var group = parsed.Groups[0];
        var grown = group with { States = [.. group.States, new State(7, [new Condition(0, Evaluator: [0xA1])])] };
        var back = Esd.Read(Esd.Write(parsed with { Groups = [grown] }));
        back.Groups[0].States.Select(s => s.Id).Should().Equal(0L, 1L, 7L);
        back.Groups[0].States[2].Conditions[0].Target.Should().Be(0);
        back.Groups[0].States[0].Conditions[0].Target.Should().Be(1);
    }

    [Fact]
    public void WriteStillAcceptsAFreshConditionOnAnExistingState()
    {
        // The condition pool is an indirection table, so a state's reservation covers its whole list at once.
        var parsed = Esd.Read(Synthetic((1, [(0, 1), (1, null)])));
        var group = parsed.Groups[0];
        var state0 = group.States[0] with { Conditions = [.. group.States[0].Conditions, new Condition(1, Evaluator: [0xA2])] };
        var back = Esd.Read(Esd.Write(parsed with { Groups = [group with { States = [state0, group.States[1]] }] }));
        back.Groups[0].States[0].Conditions.Select(c => c.Target).Should().Equal(1L, 1L);
    }

    [Fact]
    public void WriteStillAcceptsAWhollyFreshStateWithCallsAndArgs()
    {
        var parsed = Esd.Read(Synthetic((1, [(0, 1), (1, null)])));
        var group = parsed.Groups[0];
        var fresh = new State(7, Entry: [new CommandCall(1, 42, [new CommandArg([0x09])])]);
        var back = Esd.Read(Esd.Write(parsed with { Groups = [group with { States = [.. group.States, fresh] }] }));
        var added = back.Groups[0].States[^1];
        added.Id.Should().Be(7);
        (added.Entry[0].Bank, added.Entry[0].Id).Should().Be((1, 42));
        added.Entry[0].Args[0].Bytecode.Should().Equal(0x09);
    }

    // ---- write: refusals

    [Fact]
    public void WriteRefusesAFreshCallAppendedToAnExistingStatesEntry()
    {
        // The fresh call would land at the tail of the call table, so the state's run would no longer be contiguous
        // and a reader walking forward from its first slot would pick up a neighbour's call.
        var stateA = new State(0, Entry: [new CommandCall(1, 100, Order: 0), new CommandCall(1, 300)], Order: 0);
        var stateB = new State(1, Entry: [new CommandCall(1, 200, Order: 1)], Order: 1);
        var act = () => Esd.Write(Archive(new StateGroup(1, [stateA, stateB], 0)));
        act.Should().Throw<EsdException>().WithMessage("*contiguous*");
    }

    [Fact]
    public void WriteRefusesAFreshArgAppendedToAnExistingCallsArgs()
    {
        var callA = new CommandCall(1, 100, [new CommandArg([1], 0), new CommandArg([3])], 0);
        var callB = new CommandCall(1, 200, [new CommandArg([2], 1)], 1);
        var act = () => Esd.Write(Archive(new StateGroup(1, [new State(0, Entry: [callA, callB], Order: 0)], 0)));
        act.Should().Throw<EsdException>().WithMessage("*arg table*");
    }

    [Fact]
    public void WriteRefusesTwoDifferentConditionsSharingAnOrder()
    {
        // Order is only unique within the file a record came from, so a merge that forwards a grafted record's
        // order can collide with a different record; keeping one would be quietly wrong.
        var state = new State(0, [new Condition(null, Evaluator: [0xA1], Order: 5), new Condition(null, Evaluator: [0xA2], Order: 5)], Order: 0);
        var act = () => Esd.Write(Archive(new StateGroup(1, [state], 0)));
        act.Should().Throw<EsdException>().WithMessage("*table index 5*");
    }

    [Fact]
    public void WriteRefusesAJumpTargetTheGroupDoesNotHave()
    {
        var state = new State(0, [new Condition(99, Evaluator: [0x41, 0xA1])]);
        var act = () => Esd.Write(Archive(new StateGroup(1, [state])));
        act.Should().Throw<EsdException>().WithMessage("*99*");
    }

    [Fact]
    public void WriteRefusesAGroupWithNoStates()
    {
        var act = () => Esd.Write(Archive(new StateGroup(1, [])));
        act.Should().Throw<EsdException>().WithMessage("*no states*");
    }

    // ---- first state

    [Fact]
    public void FirstStateIsTheLowestOrderRowNotTheFirstInTheList()
    {
        var late = new State(7, Order: 3);
        var early = new State(4, Order: 1);
        Esd.FirstState(new StateGroup(1, [late, early])).Should().BeSameAs(early);
    }

    [Fact]
    public void FirstStateFallsBackToListOrderForFreshRecords()
    {
        Esd.FirstState(new StateGroup(1, [new State(4), new State(7)])).Id.Should().Be(4);
    }

    [Fact]
    public void WritePointsTheGroupHeaderAtTheLowestOrderState()
    {
        var group = new StateGroup(1, [new State(7, Order: 3), new State(4, Order: 1)]);
        var back = Esd.Read(Esd.Write(new EsdFile([group], "t000001000", [0u, 0u, 0u, 0u])));
        back.Groups[0].States.Select(s => s.Id).Should().Equal(4L, 7L);
    }

    [Fact]
    public void ASingleStateGroupRoundTripsWithoutADummyRow()
    {
        // Every group in the shipped files has at least two states, so no file on disk takes this branch.
        var group = new StateGroup(1, [new State(0, [new Condition(0, Evaluator: [0x41, 0xA1])])]);
        var raw = Esd.Write(new EsdFile([group], "t000001000", [0u, 0u, 0u, 0u]));
        var back = Esd.Read(raw);
        back.Groups[0].States.Select(s => s.Id).Should().Equal(0L);
        back.Groups[0].States[0].Conditions[0].Target.Should().Be(0);
        Esd.Write(back).Should().Equal(raw);
    }

    // ---- corrupt input

    [Fact]
    public void ReadRefusesANameThatRunsPastTheEnd()
    {
        var raw = Synthetic((1, [(0, 1), (1, null)]));
        ((Action)(() => Esd.Read(EsdFixture.Corrupt(raw, 0x58, 4000, 4)))).Should().Throw<EsdException>().WithMessage("*name*");
        ((Action)(() => Esd.Read(EsdFixture.Corrupt(raw, 0x54, 0x7000_0000, 4)))).Should().Throw<EsdException>().WithMessage("*name*");
    }

    [Fact]
    public void ReadRefusesAStateRunThatLeavesItsTable()
    {
        var raw = Synthetic((1, [(0, 1), (1, null)]));
        var groupAt = Esd.HeaderSize + Esd.InternalHeaderSize;
        var statesAt = Esd.InternalHeaderSize + Esd.GroupSize;
        var act = () => Esd.Read(EsdFixture.Corrupt(raw, groupAt + 8, statesAt + 40 * Esd.StateSize, 8));
        act.Should().Throw<EsdException>().WithMessage("*state run*");
    }

    [Fact]
    public void ReadRefusesAStateRunThatIsNotOnARowBoundary()
    {
        var raw = Synthetic((1, [(0, 1), (1, null)]));
        var groupAt = Esd.HeaderSize + Esd.InternalHeaderSize;
        var statesAt = Esd.InternalHeaderSize + Esd.GroupSize;
        var act = () => Esd.Read(EsdFixture.Corrupt(raw, groupAt + 8, statesAt + 3, 8));
        act.Should().Throw<EsdException>().WithMessage("*row boundary*");
    }

    [Fact]
    public void ReadRefusesAConditionPoolRunOutsideThePool()
    {
        // The pool is bounded against the file, not its declared count, because vanilla overstates that count.
        var raw = Synthetic((1, [(0, 1), (1, null)]));
        var statesAt = Esd.HeaderSize + Esd.InternalHeaderSize + Esd.GroupSize;
        var act = () => Esd.Read(EsdFixture.Corrupt(raw, statesAt + 8, 0x7000_0000, 8));
        act.Should().Throw<EsdException>().WithMessage("*condition pool*");
    }

    [Fact]
    public void ReadRefusesAnEvaluatorThatRunsPastTheEnd()
    {
        // A truncated evaluator decodes to a different machine, which nothing downstream could notice.
        var raw = Synthetic((1, [(0, 1), (1, null)]));
        var condsAt = Esd.HeaderSize + Esd.InternalHeaderSize + Esd.GroupSize + 3 * Esd.StateSize;
        var act = () => Esd.Read(EsdFixture.Corrupt(raw, condsAt + 48, 4000, 8));
        act.Should().Throw<EsdException>().WithMessage("*evaluator*");
    }

    [Fact]
    public void ReadRefusesAConditionThatIsItsOwnSubcondition()
    {
        // Ancestors only, not every row seen: shipped files point several pools at one condition row.
        var raw = Synthetic((1, [(0, 1), (1, null)]));
        var condsRel = Esd.InternalHeaderSize + Esd.GroupSize + 3 * Esd.StateSize;
        var condsAt = Esd.HeaderSize + condsRel;
        // Slot 0 of the pool already holds condition 0's own row offset.
        raw = EsdFixture.Corrupt(raw, condsAt + 24, condsRel + Esd.ConditionSize, 8);
        raw = EsdFixture.Corrupt(raw, condsAt + 32, 1, 8);
        var act = () => Esd.Read(raw);
        act.Should().Throw<EsdException>().WithMessage("*cycle*");
    }

    private static byte[] ConditionChain(int length)
    {
        var condition = new Condition(null, Evaluator: [0xA1]);
        for (var i = 1; i < length; i++) condition = new Condition(null, Subconditions: [condition], Evaluator: [0xA1]);
        return Esd.Write(Archive(new StateGroup(1, [new State(0, [condition])])));
    }

    [Fact]
    public void ReadRefusesAConditionChainDeeperThanItWillRecurse()
    {
        // A chain of distinct rows has no cycle, so only a depth bound stops it recursing once per row.
        var act = () => Esd.Read(ConditionChain(1001));
        act.Should().Throw<EsdException>().WithMessage("*deeper than*");
    }

    [Fact]
    public void ReadAcceptsAConditionChainAtTheDepthBound()
    {
        var parsed = Esd.Read(ConditionChain(1000));
        EsdFixture.WalkConditions(parsed.Groups[0].States[0].Conditions).Should().HaveCount(1000);
    }

    [Fact]
    public void WriteRefusesAnUnknownHeaderThatIsNotFourWords()
    {
        var act = () => Esd.Write(new EsdFile([new StateGroup(1, [new State(0)])], "", [0u, 0u, 0u]));
        act.Should().Throw<EsdException>().WithMessage("*four*");
    }
}
