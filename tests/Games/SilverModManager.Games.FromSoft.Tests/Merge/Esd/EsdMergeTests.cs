using FluentAssertions;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Merge;
using static NexusMods.Games.FromSoft.Tests.Merge.EsdMergeFixture;
using EsdFormat = NexusMods.Games.FromSoft.Formats.Esd;

namespace NexusMods.Games.FromSoft.Tests.Merge;

public class EsdMergeTests
{
    private static (EsdFile Merged, IReadOnlyList<EsdNote> Notes) Merge(EsdFile @base, EsdFile other, EsdFile vanilla) =>
        EsdMerge.Merge(@base, other, vanilla);

    private static IReadOnlyList<long> StateIds(EsdFile file, long group) => GroupOf(file, group).States.Select(s => s.Id).ToList();

    private static void AssertCallDegrafted(CommandCall call)
    {
        call.Order.Should().BeNull();
        foreach (var arg in call.Args)
        {
            arg.Order.Should().BeNull();
            arg.BlobOrder.Should().BeNull();
        }
    }

    private static void AssertConditionDegrafted(Condition condition)
    {
        condition.Order.Should().BeNull();
        condition.BlobOrder.Should().BeNull();
        foreach (var call in condition.PassCommands) AssertCallDegrafted(call);
        foreach (var sub in condition.Subconditions) AssertConditionDegrafted(sub);
    }

    // --- decisions made on a whole group ---

    [Fact]
    public void AGroupOnlyTheOtherSideAddedIsGrafted()
    {
        var vanilla = Archive((1, [(0, null)]));
        var @base = Archive((1, [(0, null)]));
        var other = Archive((1, [(0, null)]), (1000, [(0, null), (1, 0)]));
        var (merged, notes) = Merge(@base, other, vanilla);
        Ids(merged).Should().Equal(1L, 1000L);
        // The group's contents, not just its id: a graft that arrives as an empty shell of the right number is the
        // failure this guards against.
        var grafted = GroupOf(merged, 1000);
        grafted.States.Select(s => s.Id).Should().Equal(0L, 1L);
        Targets(Find(grafted, 1)).Should().Equal(0L);
        notes.Should().BeEmpty();
    }

    [Fact]
    public void AGroupTheBaseAddedSurvivesTheMerge()
    {
        // The three-way is what stops the other side's untouched copy reverting work the base did; a two-way union
        // would drop these.
        var vanilla = Archive((1, [(0, null)]));
        var @base = Archive((1, [(0, null)]), (88, [(0, null)]));
        var other = Archive((1, [(0, null)]));
        Ids(Merge(@base, other, vanilla).Merged).Should().Equal(1L, 88L);
    }

    [Fact]
    public void AGroupTheOtherSideDeletedIsDeleted()
    {
        var vanilla = Archive((1, [(0, null)]), (85, [(0, null)]));
        var @base = Archive((1, [(0, null)]), (85, [(0, null)]));
        var other = Archive((1, [(0, null)]));
        Ids(Merge(@base, other, vanilla).Merged).Should().Equal(1L);
    }

    [Fact]
    public void AGroupOnlyTheOtherSideModifiedIsTaken()
    {
        var vanilla = Archive((1, [(0, null)]), (32, [(0, null)]));
        var @base = Archive((1, [(0, null)]), (32, [(0, null)]));
        var other = Archive((1, [(0, null)]), (32, [(0, null), (1, 0)]));
        StateIds(Merge(@base, other, vanilla).Merged, 32).Should().Equal(0L, 1L);
    }

    [Fact]
    public void AGroupOnlyTheBaseModifiedKeepsTheBaseVersion()
    {
        var vanilla = Archive((1, [(0, null)]), (32, [(0, null)]));
        var @base = Archive((1, [(0, null)]), (32, [(0, null), (1, 0)]));
        var other = Archive((1, [(0, null)]), (32, [(0, null)]));
        StateIds(Merge(@base, other, vanilla).Merged, 32).Should().Equal(0L, 1L);
    }

    [Fact]
    public void AGroupBothSidesInventedDifferentlyIsRefused()
    {
        // No vanilla to express either side's edit against, so there is no delta to replay. Taking one side's graph
        // whole could leave the other side's grafts dangling, which makes this a graph-integrity refusal and not a
        // pick-a-winner case.
        var vanilla = Archive((1, [(0, null)]));
        var @base = Archive((1, [(0, null)]), (24, [(0, null), (1, 0)]));
        var other = Archive((1, [(0, null)]), (24, [(0, null), (2, 0)]));
        var act = () => Merge(@base, other, vanilla);
        act.Should().Throw<EsdMergeException>().WithMessage("*added by both mods*");
    }

    [Fact]
    public void AGroupTheBaseDeletedAndTheOtherChangedSaysWhichHappened()
    {
        // The base is the higher-priority side, so its deletion stands, and the note says the other mod's change to
        // the group was the casualty.
        var vanilla = Archive((1, [(0, null)]), (85, [(0, 1), (1, null)]));
        var @base = Archive((1, [(0, null)]));
        var other = Archive((1, [(0, null)]), (85, [(0, null), (1, null)]));
        var (merged, notes) = Merge(@base, other, vanilla);
        Ids(merged).Should().Equal(1L);
        notes.Should().ContainSingle().Which.Should().Be(new EsdNote(85, null, EsdNoteReason.GroupDeletedByBase));
    }

    [Fact]
    public void AGroupTheOtherDeletedAndTheBaseChangedSaysWhichHappened()
    {
        var vanilla = Archive((1, [(0, null)]), (85, [(0, 1), (1, null)]));
        var @base = Archive((1, [(0, null)]), (85, [(0, null), (1, null)]));
        var other = Archive((1, [(0, null)]));
        var (merged, notes) = Merge(@base, other, vanilla);
        Ids(merged).Should().Equal(1L, 85L);
        Targets(Find(GroupOf(merged, 85), 0)).Should().BeEmpty("the base's changed version stays");
        notes.Should().ContainSingle().Which.Should().Be(new EsdNote(85, null, EsdNoteReason.GroupDeletedByOther));
    }

    [Fact]
    public void AGroupBothModsDeletedStaysDeleted()
    {
        // Agreement, not a conflict, and it must not reach the group merge, which has no version of the group to
        // work from at all.
        var vanilla = Archive((1, [(0, null)]), (85, [(0, null)]));
        var @base = Archive((1, [(0, null)]));
        var other = Archive((1, [(0, null)]));
        var (merged, notes) = Merge(@base, other, vanilla);
        Ids(merged).Should().Equal(1L);
        notes.Should().BeEmpty();
    }

    [Fact]
    public void AGroupBothModsAddedIdenticallyIsTakenOnce()
    {
        var vanilla = Archive((1, [(0, null)]));
        EsdFile Added() => Archive((1, [(0, null)]), (1000, [(0, 1), (1, null)]));
        var (merged, notes) = Merge(Added(), Added(), vanilla);
        Ids(merged).Should().Equal(1L, 1000L);
        StateIds(merged, 1000).Should().Equal(0L, 1L);
        notes.Should().BeEmpty();
    }

    // --- grafted records carry no table position of the file they came from ---

    [Fact]
    public void AGraftedGroupCarriesNoLeftoverOrderAndSurvivesWrite()
    {
        // The real check: a merge whose output cannot serialise is not a merge. The other file's group carries
        // table positions from its own read; reusing them would collide with the base's, which number from zero too.
        var vanilla = Archive((1, [(0, null)]));
        var @base = Archive((1, [(0, null)]));
        var other = Machines(
            new StateGroup(1, [new State(0, Order: 0)], Order: 0),
            new StateGroup(1000,
            [
                new State(0, Order: 1, Conditions: [new Condition(1, Evaluator: Always, Order: 0, BlobOrder: 0)]),
                new State(1, Order: 2, Entry: [new CommandCall(6, 1000, [new CommandArg([0x40, 0xA1], Order: 0, BlobOrder: 100)], Order: 0)]),
            ], Order: 1));

        var (merged, _) = Merge(@base, other, vanilla);
        foreach (var state in GroupOf(merged, 1000).States)
        {
            state.Order.Should().BeNull();
            foreach (var condition in state.Conditions) AssertConditionDegrafted(condition);
            foreach (var call in state.Entry.Concat(state.Exit).Concat(state.While)) AssertCallDegrafted(call);
        }

        Ids(EsdFormat.Read(EsdFormat.Write(merged))).Should().Equal(1L, 1000L);
    }

    [Fact]
    public void AGraftedConditionDegraftsThroughSubconditionsAndPassCommands()
    {
        // Every nested record carries a table position that collides with a real record the base owns in the same
        // table but with different content. Left uncleared, any one of them is indistinguishable from a genuine
        // cross-file collision and the writer refuses it.
        var group1 = new StateGroup(1,
        [
            new State(0, Order: 0,
                Conditions: [new Condition(null, Evaluator: [0x42, 0xA1], Order: 0, BlobOrder: 0)],
                Entry: [new CommandCall(9, 9, [new CommandArg([0x43, 0xA1], Order: 0, BlobOrder: 0)], Order: 0)]),
        ], Order: 0);
        var vanilla = Machines(group1);
        var graftArg = new CommandArg([0x44, 0xA1], Order: 0, BlobOrder: 0);
        var graftCall = new CommandCall(1, 1, [graftArg], Order: 0);
        var graftSub = new Condition(null, Evaluator: [0x45, 0xA1], Order: 0, BlobOrder: 0);
        var graftCond = new Condition(1, [graftCall], [graftSub], [0x46, 0xA1], Order: 50, BlobOrder: 50);
        var other = Machines(group1, new StateGroup(1000, [new State(0, [graftCond], Order: 1), new State(1, Order: 2)], Order: 1));

        var (merged, _) = Merge(vanilla, other, vanilla);
        foreach (var state in GroupOf(merged, 1000).States)
        {
            state.Order.Should().BeNull();
            foreach (var condition in state.Conditions) AssertConditionDegrafted(condition);
            foreach (var call in state.Entry.Concat(state.Exit).Concat(state.While)) AssertCallDegrafted(call);
        }

        Ids(EsdFormat.Read(EsdFormat.Write(merged))).Should().Equal(1L, 1000L);
    }

    // --- change detection ---

    [Fact]
    public void AGroupThreeEncodersSpellDifferentlyIsNotAConflict()
    {
        // Nobody edited this group; each of the three files writes "always" its own way. Read literally that is two
        // conflicting edits and the merge would refuse a machine none of the three disagrees about.
        EsdFile Spelling(byte[] evaluator) =>
            Machines(new StateGroup(24, [new State(0, [new Condition(1, Evaluator: evaluator)]), new State(1)]));

        var (merged, notes) = Merge(Spelling(AlwaysInt32), Spelling(AlwaysEqOne), Spelling(Always));
        notes.Should().BeEmpty();
        Find(GroupOf(merged, 24), 0).Conditions[0].Evaluator.Should().Equal(AlwaysInt32);
    }

    [Fact]
    public void AModWhoseOnlyEditIsACommandArgumentIsNotDropped()
    {
        // Canonical form drives change detection, so an edit it cannot see is an edit that never happened.
        EsdFile Spelling(byte[] blob) => Machines(new StateGroup(24, [Calling(blob)]));

        var (merged, notes) = Merge(Spelling([0x42, 0xA1]), Spelling([0x43, 0xA1]), Spelling([0x42, 0xA1]));
        var shipped = GroupOf(merged, 24).States[0].Entry.Single();
        shipped.Args.Select(a => a.Bytecode).Should().ContainSingle().Which.Should().Equal(0x43, 0xA1);
        notes.Should().BeEmpty();
    }

    [Fact]
    public void AGroupWithAnEmptyEvaluatorMergesInsteadOfCrashing()
    {
        EsdFile Spelling(long target) => Machines(new StateGroup(24,
            [new State(0, [new Condition(target, Evaluator: []), new Condition(target, Evaluator: [])]), new State(1)]));

        var (merged, notes) = Merge(Spelling(1), Spelling(1), Spelling(1));
        notes.Should().BeEmpty();
        StateIds(merged, 24).Should().Equal(0L, 1L);
    }

    // --- delta replay ---

    [Fact]
    public void SharedGroupReplaysAnAddedStateAndItsHook()
    {
        // Melina's shape: she adds a state and redirects an existing transition into it. The redirect has to land
        // on the base's copy of that state.
        var vanilla = Archive((24, [(15, null), (26, 15)]));
        var @base = Archive((24, [(15, null), (26, 15), (99, 15)])); // base added its own
        var other = With(Archive((24, [(15, null), (26, 44)])), 24, StateOf(44, Cond(15, Guard)));

        var (merged, notes) = Merge(@base, other, vanilla);
        var group = GroupOf(merged, 24);
        group.States.Select(s => s.Id).Should().Contain([44L, 99L]); // both sides' additions survive
        Targets(Find(group, 26)).Should().Equal(44L); // the redirect was replayed
        Targets(Find(group, 44)).Should().Equal(15L); // and the graft still lands
        notes.Should().BeEmpty();
    }

    [Fact]
    public void SharedGroupNotesAStateTheBaseDeleted()
    {
        // Where the other side edits a state the base removed, there is nowhere to replay it. Keep the base and say
        // so; silently dropping it is the failure the notes exist to prevent.
        var vanilla = Archive((24, [(15, null), (26, 15), (27, 15)]));
        var @base = Archive((24, [(15, null), (26, 15)])); // deleted 27
        var other = With(Archive((24, [(15, null), (26, 15), (27, 999)])), 24, StateOf(999));

        var (merged, notes) = Merge(@base, other, vanilla);
        notes.Should().Contain(n => n.Group == 24 && n.State == 27 && n.Reason == EsdNoteReason.NotInBase);
        StateIds(merged, 24).Should().Equal(15L, 26L, 999L);
    }

    [Fact]
    public void AStateNeitherModKeptIsNotReportedAsAKeptCopy()
    {
        // "The preferred mod's copy was kept" is a claim about what shipped; when both mods dropped the state there
        // is no copy to have kept.
        var distinctive = StateOf(2, new Condition(1, Evaluator: GuardOrGuard));
        var vanilla = OneGroup(Hook(0, 1), StateOf(1), distinctive);
        // Both mods drop vanilla 2, and each changes something else, so the group still has to be merged state by state.
        var @base = OneGroup(Hook(0, 1), StateOf(1), StateOf(8, new Condition(1, Evaluator: Other)));
        var other = OneGroup(Hook(0, 3), StateOf(1), StateOf(3, new Condition(1, Evaluator: Guard)));

        var (merged, notes) = Merge(@base, other, vanilla);
        StateIds(merged, 24).Should().NotContain(2L);
        notes.Select(n => (n.State, n.Reason)).Should().Equal((2L, EsdNoteReason.NotInEither));
    }

    [Fact]
    public void AStateOnlyTheOtherModDroppedStillReportsTheKeptCopy()
    {
        var distinctive = StateOf(2, new Condition(1, Evaluator: GuardOrGuard));
        var vanilla = OneGroup(Hook(0, 1), StateOf(1), distinctive);
        var @base = OneGroup(Hook(0, 1), StateOf(1), distinctive, StateOf(8, new Condition(1, Evaluator: Other)));
        var other = OneGroup(Hook(0, 3), StateOf(1), StateOf(3, new Condition(1, Evaluator: Guard)));

        var (merged, notes) = Merge(@base, other, vanilla);
        StateIds(merged, 24).Should().Contain(2L);
        notes.Select(n => (n.State, n.Reason)).Should().Equal((2L, EsdNoteReason.NotInOther));
    }

    [Fact]
    public void AGraftedStateGetsAnIdThatIsFreeInTheBase()
    {
        // Both sides added a state under the same id. Renumbering the graft is the only option, and every reference
        // to it has to move with it.
        var vanilla = Archive((24, [(0, 9), (9, null)]));
        var @base = Archive((24, [(0, 9), (9, null), (1, 9)])); // base's own state 1
        var other = With(Archive((24, [(0, 1), (9, null)])), 24, StateOf(1, Cond(9, Guard))); // hooks its own state 1

        var (merged, _) = Merge(@base, other, vanilla);
        var group = GroupOf(merged, 24);
        group.States.Select(s => s.Id).Should().OnlyHaveUniqueItems();
        var grafted = Targets(Find(group, 0)).Should().ContainSingle().Subject;
        grafted.Should().NotBe(1, "the base already owns 1");
        Targets(Find(group, grafted)).Should().Equal(9L);
        Targets(Find(group, 1)).Should().Equal(9L); // the base's own state 1 is untouched
    }

    [Fact]
    public void BothModsChangingOneStateKeepsTheBaseAndNamesIt()
    {
        var vanilla = Archive((24, [(0, 1), (1, null), (2, null)]));
        var @base = Archive((24, [(0, 2), (1, null), (2, null)])); // redirected to 2
        var other = With(Archive((24, [(0, 3), (1, null), (2, null)])), 24, StateOf(3)); // redirected to its own 3

        var (merged, notes) = Merge(@base, other, vanilla);
        Targets(Find(GroupOf(merged, 24), 0)).Should().Equal(2L); // base wins
        notes.Should().Contain(n => n.Group == 24 && n.State == 0 && n.Reason == EsdNoteReason.BothChanged);
    }

    [Fact]
    public void AnEditAlignmentCannotPlaceIsNamedRatherThanDropped()
    {
        // Alignment pairs states on content, so a state whose commands changed reads as a delete plus an add rather
        // than an edit; there is nothing left that looks like the vanilla state to replay onto. The edit must not
        // vanish.
        EsdFile One(int entryId, params State[] extra) => OneGroup(
            [new State(0, [new Condition(1, Evaluator: Always)], [new CommandCall(1, entryId)]), StateOf(1), .. extra]);

        var vanilla = One(100);
        var @base = One(100, StateOf(2, new Condition(1, Evaluator: Guard))); // base changed elsewhere
        var other = One(200); // other changed the call

        var (merged, notes) = Merge(@base, other, vanilla);
        notes.Should().Contain(n => n.Group == 24 && n.State == 0 && n.Reason == EsdNoteReason.NotInOther);
        // The other half of the same limitation: the rewritten state is also grafted and nothing reaches it. A dead
        // machine in the output is only tolerable because it is named, so the naming is what gets pinned.
        var orphan = notes.Single(n => n.Reason == EsdNoteReason.OrphanedGraft);
        Reachable(GroupOf(merged, 24)).Should().NotContain(orphan.Target!.Value);
    }

    [Fact]
    public void AReplayedStateCarriesNoLeftoverOrder()
    {
        // A replayed state's records come from the other file's tables, and table positions only mean something
        // inside the file they were read from. Every record the other state 0 carries is stamped with the position
        // the base uses for a different record of the same kind; left uncleared each looks like a genuine shared row
        // and the writer refuses the lot.
        State Replayable(long target, int? index, long? blobIndex, int callId, byte[] blob) =>
            new(0, Order: 0, Conditions:
            [
                new Condition(target, Evaluator: Always, Order: index, BlobOrder: blobIndex, PassCommands:
                [
                    new CommandCall(1, callId, [new CommandArg(blob, Order: index, BlobOrder: blobIndex)], Order: index),
                ]),
            ]);

        byte[] one = [0x41, 0xA1];
        var vanilla = Machines(new StateGroup(24, [Replayable(1, null, null, 7, one), StateOf(1)]));
        var @base = Machines(new StateGroup(24,
        [
            Replayable(1, 0, 0, 7, one),
            new State(1, Order: 1),
            // The base's own addition, and the owner of every table position the other state 0 also claims.
            new State(2, Order: 2, Conditions:
            [
                new Condition(1, Evaluator: Guard, Order: 1, BlobOrder: 1, PassCommands:
                [
                    new CommandCall(1, 8, [new CommandArg([0x42, 0xA1], Order: 1, BlobOrder: 2)], Order: 1),
                ]),
            ]),
        ], Order: 0));
        var other = Machines(new StateGroup(24,
        [
            Replayable(3, 1, 1, 7, one), // redirected to a state she adds
            new State(1, Order: 1),
            new State(3, Order: 2),
        ], Order: 0));

        var (merged, notes) = Merge(@base, other, vanilla);
        notes.Should().BeEmpty();
        var replayed = Find(GroupOf(merged, 24), 0);
        Targets(replayed).Should().Equal(3L);
        foreach (var condition in replayed.Conditions) AssertConditionDegrafted(condition);
        EsdFormat.Read(EsdFormat.Write(merged)).Groups[0].States.Select(s => s.Id).Should().BeEquivalentTo([0L, 1L, 2L, 3L]);
    }

    // --- refusals and orphans ---

    [Fact]
    public void AGraftThatJumpsAtADeletedStateIsRefused()
    {
        // A new state whose jump has nowhere to land cannot be grafted quietly: blanking the target turns a branch
        // into a fallthrough, which is a different machine that still loads.
        var vanilla = Archive((24, [(0, 1), (1, null), (2, null)]));
        var @base = Archive((24, [(0, 1), (1, null)])); // deleted 2
        var other = With(Archive((24, [(0, 1), (1, null), (2, null)])), 24, StateOf(3, Cond(2, Guard)));

        var act = () => Merge(@base, other, vanilla);
        act.Should().Throw<EsdMergeException>();
    }

    [Fact]
    public void AReplayThatJumpsAtADeletedStateKeepsTheBaseAndNamesIt()
    {
        var vanilla = Archive((24, [(0, 1), (1, null), (2, null)]));
        var @base = Archive((24, [(0, 1), (1, null)])); // deleted 2
        var other = Archive((24, [(0, 2), (1, null), (2, null)])); // redirected 0 at it

        var (merged, notes) = Merge(@base, other, vanilla);
        Targets(Find(GroupOf(merged, 24), 0)).Should().Equal(1L); // the base's, untouched
        notes.Should().Contain(n => n.Group == 24 && n.State == 0 && n.Reason == EsdNoteReason.UnplaceableJump && n.Target == 2);
    }

    [Fact]
    public void StatesAlignmentCouldNotTellApartAreNamedNotDuplicated()
    {
        // Unresolved is not RightOnly. Treating a state the alignment gave up on as an addition grafts a second copy
        // of a machine that is already there.
        State LookAlike(long id) => StateOf(id, new Condition(7, Evaluator: Other));
        var anchor = StateOf(0, new Condition(7, Evaluator: Guard));
        var vanilla = OneGroup(anchor, LookAlike(1), LookAlike(2), StateOf(7));
        var @base = OneGroup(anchor, LookAlike(1), LookAlike(2), StateOf(7), StateOf(8, new Condition(7, Evaluator: Guard)));
        var other = OneGroup(anchor, LookAlike(5), LookAlike(6), StateOf(7));

        var (merged, notes) = Merge(@base, other, vanilla);
        StateIds(merged, 24).Should().Equal(0L, 1L, 2L, 7L, 8L);
        notes.Should().HaveCount(2).And.OnlyContain(n => n.Group == 24 && n.Reason == EsdNoteReason.UnresolvedInOther);
        notes.Select(n => n.State).Should().BeEquivalentTo([1L, 2L]);
    }

    [Fact]
    public void MergeRefusesToReturnAGraphThatDoesNotHoldTogether()
    {
        // The check is only worth having if it runs on the way out.
        var vanilla = Archive((1, [(0, null)]));
        var other = Archive((1, [(0, null)]));
        var @base = OneGroupAt(1, Hook(0, 99));
        var act = () => Merge(@base, other, vanilla);
        act.Should().Throw<EsdMergeException>();
    }

    private static EsdFile OneGroupAt(long group, params State[] states) => Machines(new StateGroup(group, states));

    [Fact]
    public void ARenumberedButUneditedStateIsNotReadAsASecondEdit()
    {
        // Telling "the preferred mod also changed this state" from "the preferred mod renumbered around it" decides
        // whether the other mod's edit gets replayed or refused. The base deletes vanilla 2 and its state 3 becomes
        // 2, so its state 0 jumps at 2 where vanilla's jumps at 3. Compared literally that is an edit.
        State Keep(long id, long target) => StateOf(id, new Condition(target, Evaluator: Guard));
        var vanilla = OneGroup(Hook(0, 3), Keep(1, 3), StateOf(2, new Condition(3, Evaluator: GuardOrOther)), StateOf(3));
        var @base = OneGroup(Hook(0, 2), Keep(1, 2), StateOf(2)); // dropped vanilla 2
        var other = OneGroup(Hook(0, 4), Keep(1, 3), // redirected 0 at a new state
            StateOf(2, new Condition(3, Evaluator: GuardOrOther)), StateOf(3),
            StateOf(4, new Condition(3, Evaluator: Other)));

        var (merged, notes) = Merge(@base, other, vanilla);
        var group = GroupOf(merged, 24);
        notes.Should().BeEmpty();
        Targets(Find(group, 0)).Should().Equal(4L); // the redirect replayed
        Targets(Find(group, 4)).Should().Equal(2L); // onto the base's numbering
    }

    [Fact]
    public void AnEditWithNoSingleHomeInTheBaseIsNotReportedAsADeletion()
    {
        // Falling through to the deletion path keeps the right state but says the wrong thing about why. "I deleted
        // it" sends someone to the preferred mod's diff; "I could not tell which of these it is" is the alignment
        // admitting it gave up, which is a different thing to go fix.
        State LookAlike(long id, long target = 7) => StateOf(id, new Condition(target, Evaluator: Other));
        var anchor = StateOf(0, new Condition(7, Evaluator: Guard));
        var vanilla = OneGroup(anchor, LookAlike(1), LookAlike(2), StateOf(7));
        var @base = OneGroup(anchor, LookAlike(5), LookAlike(6), StateOf(7)); // renumbered
        var other = OneGroup(anchor, LookAlike(1, 8), LookAlike(2), StateOf(7), StateOf(8)); // edited 1

        var (_, notes) = Merge(@base, other, vanilla);
        var note = notes.Should().ContainSingle().Subject;
        note.Group.Should().Be(24);
        note.State.Should().Be(1);
        note.Reason.Should().Be(EsdNoteReason.UnresolvedInBase);
    }

    // --- grafts that arrive dead ---

    [Fact]
    public void AGraftOrphanedByARefusedReplayIsNamed()
    {
        // The hook that would have entered the other mod's new state lost a conflict, so the state is grafted with
        // nothing left pointing at it. Structurally the output is fine and the machine is dead; the note about the
        // lost hook does not say that a whole machine came across as unreachable code.
        var vanilla = Archive((24, [(0, 1), (1, null), (9, null)]));
        var @base = Archive((24, [(0, 9), (1, null), (9, null)])); // redirected 0 at 9
        var other = Archive((24, [(0, 3), (1, null), (9, null), (3, 1)])); // 0 through a new 3

        var (merged, notes) = Merge(@base, other, vanilla);
        var group = GroupOf(merged, 24);
        group.States.Select(s => s.Id).Should().Contain(3L);
        Reachable(group).Should().NotContain(3L);
        notes.Should().Contain(n => n.Group == 24 && n.State == 3 && n.Reason == EsdNoteReason.OrphanedGraft && n.Target == 3);
    }

    [Fact]
    public void AGraftTheOtherModCouldNotReachEitherIsNotNamed()
    {
        // The invariant is relative, not absolute. Shipped files are full of states nothing jumps at, so a graft
        // that was already dead in the mod it came from is not news.
        var vanilla = Archive((24, [(0, 1), (1, null)]));
        var @base = Archive((24, [(0, 1), (1, null), (8, 1)])); // base's own addition
        var other = Archive((24, [(0, 1), (1, null), (3, 1)])); // nothing points at 3

        var (merged, notes) = Merge(@base, other, vanilla);
        var group = GroupOf(merged, 24);
        group.States.Select(s => s.Id).Should().Contain(3L);
        Reachable(group).Should().NotContain(3L);
        notes.Should().BeEmpty();
    }

    [Fact]
    public void ReachabilityStartsWhereTheWriterSaysTheGroupStarts()
    {
        // A group's entry is its lowest-order state row, which is what the writer points the group header at, and
        // not whatever sits first in the list. The two agree for anything read off disk, so no real file tells them
        // apart. Here the other mod's hook into the state it adds hangs off the real entry row; starting from the
        // list's first state finds nothing and the graft reads as dead on arrival, so the note never gets written.
        EsdFile Group(params State[] states) => Machines(new StateGroup(24, states, Order: 0));
        State At(long target, int order) => new(0, [new Condition(target, Evaluator: Always)], Order: order);

        var vanilla = Group(At(1, 0), new State(1, Order: 1), new State(9, Order: 2));
        var @base = Group(At(9, 0), new State(1, Order: 1), new State(9, Order: 2));
        var other = Group(new State(1, Order: 1), // list order is not table order
            At(3, 0),
            new State(9, Order: 2),
            new State(3, [new Condition(1, Evaluator: Guard)], Order: 3));

        var (merged, notes) = Merge(@base, other, vanilla);
        StateIds(merged, 24).Should().Contain(3L);
        notes.Should().Contain(n => n.Group == 24 && n.State == 3 && n.Reason == EsdNoteReason.OrphanedGraft);
    }

    [Fact]
    public void AHookWrittenAsANestedBranchIsRetargetedToo()
    {
        // Both mods added a state 3, so the graft has to be renumbered, and the reference that has to move with it
        // is buried in a subcondition. Left alone it still resolves, to the preferred mod's unrelated state 3.
        var vanilla = OneGroup(NestedHook(1), StateOf(1));
        var @base = OneGroup(NestedHook(1), StateOf(1), StateOf(3, new Condition(1, Evaluator: Guard)));
        var other = OneGroup(NestedHook(3), StateOf(1), StateOf(3, new Condition(1, Evaluator: Other)));

        var (merged, notes) = Merge(@base, other, vanilla);
        var group = GroupOf(merged, 24);
        var grafted = Targets(Find(group, 0)).Should().ContainSingle().Subject;
        grafted.Should().NotBe(3, "the base already owns 3");
        Reachable(group).Should().Contain(grafted);
        Targets(Find(group, 3)).Should().Equal(1L); // the base's own state 3 is untouched
        notes.Should().BeEmpty();
    }

    [Fact]
    public void AGraftHookedByANestedBranchIsStillSeenAsLive()
    {
        // The relative invariant compares two reachability walks, so a walk that skips nested branches breaks both
        // sides equally and the comparison still balances, at "this graft was dead anyway". Here the graft's only
        // in-edge in the other mod is nested.
        var vanilla = OneGroup(NestedHook(1), StateOf(1), StateOf(9));
        var @base = OneGroup(NestedHook(9), StateOf(1), StateOf(9)); // took the hook
        var other = OneGroup(NestedHook(3), StateOf(1), StateOf(9), StateOf(3, new Condition(1, Evaluator: Other)));

        var (merged, notes) = Merge(@base, other, vanilla);
        Reachable(GroupOf(merged, 24)).Should().NotContain(3L);
        notes.Should().Contain(n => n.Group == 24 && n.State == 3 && n.Reason == EsdNoteReason.OrphanedGraft && n.Target == 3);
    }
}
