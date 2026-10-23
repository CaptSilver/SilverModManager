using FluentAssertions;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Merge;
using EsdFormat = SilverModManager.Games.FromSoft.Formats.Esd;
using static SilverModManager.Games.FromSoft.Tests.Merge.EsdMergeFixture;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

public class EsdCheckTests
{
    private static EsdFile Replacing(EsdFile file, params State[] states) =>
        file with { Groups = [file.Groups[0] with { States = states }] };

    [Fact]
    public void RejectsADanglingJumpTarget()
    {
        var act = () => EsdCheck.Check(Archive((1, [(0, 99)])));
        act.Should().Throw<EsdMergeException>().WithMessage("*group 1 state 0*99*");
    }

    [Fact]
    public void RejectsADanglingTargetInsideASubcondition()
    {
        // A graft's targets get rewritten recursively, so the check has to look just as deep; a nested branch is
        // where a missed rewrite would hide.
        var nested = new State(0, [new Condition(null, Subconditions: [new Condition(99, Evaluator: Always)], Evaluator: Guard)]);
        var act = () => EsdCheck.Check(Replacing(Archive((1, [(0, null)])), nested));
        act.Should().Throw<EsdMergeException>().WithMessage("*99*");
    }

    [Fact]
    public void RejectsDuplicateStateIds()
    {
        var file = Archive((1, [(0, null)]));
        var act = () => EsdCheck.Check(Replacing(file, [.. file.Groups[0].States, .. file.Groups[0].States]));
        act.Should().Throw<EsdMergeException>().WithMessage("*more than one state numbered 0*");
    }

    [Fact]
    public void RejectsACallToAMissingMachine()
    {
        var caller = new State(0, Entry: [new CommandCall(EsdFormat.CallBank, 2147482648)]);
        var act = () => EsdCheck.Check(Replacing(Archive((1, [(0, null)])), caller));
        act.Should().Throw<EsdMergeException>().WithMessage("*calls machine 2147482648*");
    }

    [Fact]
    public void IgnoresOtherBanks()
    {
        // Only bank 6 command ids name a state group. The other banks are ordinary engine calls whose ids collide
        // with group ids by coincidence.
        var caller = new State(0, Entry: [new CommandCall(1, 2147482648)]);
        EsdCheck.Check(Replacing(Archive((1, [(0, null)])), caller));
    }

    [Fact]
    public void RejectsACallFromAPassCommand()
    {
        var caller = new State(0, [new Condition(null, [new CommandCall(EsdFormat.CallBank, 77)], Evaluator: Always)]);
        var act = () => EsdCheck.Check(Replacing(Archive((1, [(0, null)])), caller));
        act.Should().Throw<EsdMergeException>().WithMessage("*calls machine 77*");
    }

    [Fact]
    public void AcceptsACallToAMachineTheFileHolds()
    {
        var caller = new State(0, Entry: [new CommandCall(EsdFormat.CallBank, 2)]);
        EsdCheck.Check(Archive((1, [(0, null)]), (2, [(0, null)])) with
        {
            Groups = [new StateGroup(1, [caller]), new StateGroup(2, [new State(0)])],
        });
    }

    [Fact]
    public void ReachabilityStartsWhereTheWriterSaysTheGroupStarts()
    {
        // The entry is the lowest-order state row, which is what the writer points the group header at, and not
        // whatever sits first in the list. A walk that starts at the wrong one reports a live machine as dead.
        var group = new StateGroup(24, [new State(1, Order: 1), Hook(0, 1) with { Order = 0 }, new State(9, Order: 2)], Order: 0);
        EsdCheck.Reachable(group).Should().BeEquivalentTo([0L, 1L]);
    }
}
