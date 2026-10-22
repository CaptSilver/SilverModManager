using FluentAssertions;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Merge;
using static NexusMods.Games.FromSoft.Merge.EsdAlignment;
using static NexusMods.Games.FromSoft.Tests.Merge.EsdMergeFixture;

namespace NexusMods.Games.FromSoft.Tests.Merge;

public class EsdAlignmentTests
{
    private static StateGroup Group(params State[] states) => new(24, states);

    private static State Empty(long id) => StateOf(id);

    [Fact]
    public void MatchesStatesByIdWhenContentAgrees()
    {
        var left = Group(StateOf(0, Cond(1)), Empty(1));
        var right = Group(StateOf(0, Cond(1)), Empty(1));
        var aligned = Align(left, right);
        aligned.Pairs.Should().Equal(new Dictionary<long, long> { [0] = 0, [1] = 1 });
        aligned.LeftOnly.Should().BeEmpty();
        aligned.RightOnly.Should().BeEmpty();
        aligned.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public void FollowsContentWhenIdsWereRenumbered()
    {
        // A mod that deletes states from a shared group and renumbers the survivors down makes the same id mean
        // different things on the two sides; matching by id would pair unrelated states and corrupt every jump.
        var left = Group(StateOf(26, Cond(15)), StateOf(28, Cond(16, Other)), Empty(29));
        var right = Group(StateOf(26, Cond(16, Other)), Empty(27));
        var aligned = Align(left, right);
        aligned.Pairs[28].Should().Be(26); // same content, renumbered
        aligned.Pairs[29].Should().Be(27);
        aligned.Pairs.Should().NotContainKey(26); // left's 26 was deleted, not renamed
        aligned.LeftOnly.Should().Equal(26L);
    }

    [Fact]
    public void SeparatesADeletedStateFromOneItCouldNotPlace()
    {
        // A caller replaying a delta has to tell "this state is gone" from "the alignment gave up".
        var left = Group(StateOf(0, Cond(9, Guard)),  // nothing like it
                         StateOf(1, Cond(2, Other)),  // two identical
                         StateOf(2, Cond(2, Other))); // candidates
        var right = Group(StateOf(5, Cond(2, Other)), StateOf(6, Cond(2, Other)));
        var aligned = Align(left, right);
        aligned.LeftOnly.Should().Equal(0L);
        aligned.Unresolved.Should().Equal(1L, 2L);
        aligned.Pairs.Should().BeEmpty();
    }

    [Fact]
    public void ResolvesLookAlikeStatesByIteratingOnTheTargets()
    {
        // Renumbered on both sides, with nothing but the jump targets to tell three look-alikes apart. States 2, 3
        // and 4 are identical until their targets are known, and each target is known only once the next state is
        // paired, so the answer arrives one state per round working back from the anchor. The id ranges are
        // disjoint, so a left id read as a right id resolves to nothing.
        var left = Group(StateOf(1, Cond(2, Guard)), StateOf(2, Cond(3, Other)), StateOf(3, Cond(4, Other)),
                         StateOf(4, Cond(5, Other)), Empty(5));
        var right = Group(StateOf(11, Cond(12, Guard)), StateOf(12, Cond(13, Other)), StateOf(13, Cond(14, Other)),
                          StateOf(14, Cond(15, Other)), Empty(15));
        var aligned = Align(left, right);
        aligned.Pairs.Should().Equal(new Dictionary<long, long> { [1] = 11, [2] = 12, [3] = 13, [4] = 14, [5] = 15 });
        aligned.LeftOnly.Should().BeEmpty();
        aligned.RightOnly.Should().BeEmpty();
        aligned.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public void ARightHandStateThatIsNeitherPairedNorAddedHasALookAlike()
    {
        // A state of the right-hand file that is neither paired nor added is not reported under its own id; the
        // report names the left-hand state it could not be told apart from. That holds only while every such
        // state really has an unpaired look-alike on the left, which is what puts that state into Unresolved.
        // Stop guaranteeing it and a state the right side holds goes missing in silence.
        State LookAlike(long id) => StateOf(id, new Condition(7, Evaluator: Other));
        var left = Group(StateOf(0, new Condition(7, Evaluator: Guard)), LookAlike(1), LookAlike(2), Empty(7));
        var right = Group(StateOf(0, new Condition(7, Evaluator: Guard)), LookAlike(5), LookAlike(6), Empty(7),
                          StateOf(8, new Condition(7, Evaluator: Always)));

        var aligned = Align(left, right);
        var accounted = aligned.Pairs.Values.Concat(aligned.RightOnly).ToHashSet();
        var leftover = right.States.Select(s => s.Id).Where(id => !accounted.Contains(id)).ToList();
        leftover.Should().Equal(5L, 6L); // the look-alikes, not 8
        foreach (var id in leftover)
        {
            aligned.Unresolved.Should().Contain(v => Masked(left, v).Equals(Masked(right, id)), $"state {id}");
        }
    }

    private static RetargetedShape Masked(StateGroup group, long id) =>
        Retargeted(EsdCanonical.Canonical(group.States.Single(s => s.Id == id)), TargetMasked);
}
