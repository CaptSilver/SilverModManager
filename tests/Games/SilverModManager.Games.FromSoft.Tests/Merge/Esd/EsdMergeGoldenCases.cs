using SilverModManager.Games.FromSoft.Formats;
using static SilverModManager.Games.FromSoft.Tests.Merge.EsdMergeFixture;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

/// <summary>
/// The triples whose merge erm writes as golden bytes, each built the way the erm script that produced the golden
/// builds it. Only cases erm does not refuse are here; a case the app settles by priority has no erm output and is
/// pinned in <see cref="EsdMergeTests"/>.
/// </summary>
public static class EsdMergeGoldenCases
{
    public sealed record Triple(EsdFile Base, EsdFile Other, EsdFile Vanilla);

    public static IReadOnlyDictionary<string, Func<Triple>> All { get; } = new Dictionary<string, Func<Triple>>
    {
        ["group_decisions"] = GroupDecisions,
        ["graft_with_orders"] = GraftWithOrders,
        ["graft_nested_records"] = GraftNestedRecords,
        ["hook_replayed"] = HookReplayed,
        ["free_id"] = FreeId,
        ["no_leftover_order"] = NoLeftoverOrder,
        ["nested_hook"] = NestedHooks,
        ["orphaned_graft"] = OrphanedGraft,
        ["not_in_base"] = NotInBase,
        ["unplaceable_jump"] = UnplaceableJump,
        ["both_changed"] = BothChanged,
        ["unresolved_in_other"] = UnresolvedInOther,
        ["not_in_other_and_orphan"] = NotInOtherAndOrphan,
    };

    private static Triple GroupDecisions()
    {
        var vanilla = Archive((1, [(0, null)]), (32, [(0, null)]), (85, [(0, null)]), (90, [(0, null)]));
        var @base = Archive((1, [(0, null)]), (32, [(0, null), (1, 0)]), (85, [(0, null)]), (88, [(0, null)]),
            (90, [(0, null)]), (2000, [(0, 1), (1, null)]));
        var other = Archive((1, [(0, null)]), (32, [(0, null)]), (90, [(0, null), (1, 0)]),
            (1000, [(0, null), (1, 0)]), (2000, [(0, 1), (1, null)]));
        return new Triple(@base, other, vanilla);
    }

    private static Triple GraftWithOrders()
    {
        var vanilla = Archive((1, [(0, null)]));
        var @base = Archive((1, [(0, null)]));
        var other = Machines(
            new StateGroup(1, [new State(0, Order: 0)], Order: 0),
            new StateGroup(1000,
            [
                new State(0, Order: 1, Conditions: [new Condition(1, Evaluator: Always, Order: 0, BlobOrder: 0)]),
                new State(1, Order: 2, Entry: [new CommandCall(6, 1000, [new CommandArg([0x40, 0xA1], Order: 0, BlobOrder: 100)], Order: 0)]),
            ], Order: 1));
        return new Triple(@base, other, vanilla);
    }

    private static Triple GraftNestedRecords()
    {
        var group1 = new StateGroup(1,
        [
            new State(0, Order: 0,
                Conditions: [new Condition(null, Evaluator: [0x42, 0xA1], Order: 0, BlobOrder: 0)],
                Entry: [new CommandCall(9, 9, [new CommandArg([0x43, 0xA1], Order: 0, BlobOrder: 0)], Order: 0)]),
        ], Order: 0);
        var vanilla = Machines(group1);
        var graftCall = new CommandCall(1, 1, [new CommandArg([0x44, 0xA1], Order: 0, BlobOrder: 0)], Order: 0);
        var graftSub = new Condition(null, Evaluator: [0x45, 0xA1], Order: 0, BlobOrder: 0);
        var graftCond = new Condition(1, [graftCall], [graftSub], [0x46, 0xA1], Order: 50, BlobOrder: 50);
        var other = Machines(group1, new StateGroup(1000, [new State(0, [graftCond], Order: 1), new State(1, Order: 2)], Order: 1));
        return new Triple(vanilla, other, vanilla);
    }

    private static Triple HookReplayed() => new(
        Archive((24, [(15, null), (26, 15), (99, 15)])),
        With(Archive((24, [(15, null), (26, 44)])), 24, StateOf(44, Cond(15, Guard))),
        Archive((24, [(15, null), (26, 15)])));

    private static Triple FreeId() => new(
        Archive((24, [(0, 9), (9, null), (1, 9)])),
        With(Archive((24, [(0, 1), (9, null)])), 24, StateOf(1, Cond(9, Guard))),
        Archive((24, [(0, 9), (9, null)])));

    private static Triple NoLeftoverOrder()
    {
        State Replayable(long target, int? index, long? blobIndex, byte[] blob) =>
            new(0, Order: 0, Conditions:
            [
                new Condition(target, Evaluator: Always, Order: index, BlobOrder: blobIndex, PassCommands:
                [
                    new CommandCall(1, 7, [new CommandArg(blob, Order: index, BlobOrder: blobIndex)], Order: index),
                ]),
            ]);

        byte[] one = [0x41, 0xA1];
        var vanilla = Machines(new StateGroup(24, [Replayable(1, null, null, one), StateOf(1)]));
        var @base = Machines(new StateGroup(24,
        [
            Replayable(1, 0, 0, one),
            new State(1, Order: 1),
            new State(2, Order: 2, Conditions:
            [
                new Condition(1, Evaluator: Guard, Order: 1, BlobOrder: 1, PassCommands:
                [
                    new CommandCall(1, 8, [new CommandArg([0x42, 0xA1], Order: 1, BlobOrder: 2)], Order: 1),
                ]),
            ]),
        ], Order: 0));
        var other = Machines(new StateGroup(24,
            [Replayable(3, 1, 1, one), new State(1, Order: 1), new State(3, Order: 2)], Order: 0));
        return new Triple(@base, other, vanilla);
    }

    private static Triple NestedHooks() => new(
        OneGroup(NestedHook(1), StateOf(1), StateOf(3, new Condition(1, Evaluator: Guard))),
        OneGroup(NestedHook(3), StateOf(1), StateOf(3, new Condition(1, Evaluator: Other))),
        OneGroup(NestedHook(1), StateOf(1)));

    private static Triple OrphanedGraft() => new(
        Archive((24, [(0, 9), (1, null), (9, null)])),
        Archive((24, [(0, 3), (1, null), (9, null), (3, 1)])),
        Archive((24, [(0, 1), (1, null), (9, null)])));

    private static Triple NotInBase() => new(
        Archive((24, [(15, null), (26, 15)])),
        With(Archive((24, [(15, null), (26, 15), (27, 999)])), 24, StateOf(999)),
        Archive((24, [(15, null), (26, 15), (27, 15)])));

    private static Triple UnplaceableJump() => new(
        Archive((24, [(0, 1), (1, null)])),
        Archive((24, [(0, 2), (1, null), (2, null)])),
        Archive((24, [(0, 1), (1, null), (2, null)])));

    private static Triple BothChanged() => new(
        Archive((24, [(0, 2), (1, null), (2, null)])),
        With(Archive((24, [(0, 3), (1, null), (2, null)])), 24, StateOf(3)),
        Archive((24, [(0, 1), (1, null), (2, null)])));

    private static Triple UnresolvedInOther()
    {
        State LookAlike(long id) => StateOf(id, new Condition(7, Evaluator: Other));
        var anchor = StateOf(0, new Condition(7, Evaluator: Guard));
        return new Triple(
            OneGroup(anchor, LookAlike(1), LookAlike(2), StateOf(7), StateOf(8, new Condition(7, Evaluator: Guard))),
            OneGroup(anchor, LookAlike(5), LookAlike(6), StateOf(7)),
            OneGroup(anchor, LookAlike(1), LookAlike(2), StateOf(7)));
    }

    private static Triple NotInOtherAndOrphan()
    {
        EsdFile One(int entryId, params State[] extra) => OneGroup(
            [new State(0, [new Condition(1, Evaluator: Always)], [new CommandCall(1, entryId)]), StateOf(1), .. extra]);

        return new Triple(One(100, StateOf(2, new Condition(1, Evaluator: Guard))), One(200), One(100));
    }
}
