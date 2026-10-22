using NexusMods.Games.FromSoft.Formats;

namespace NexusMods.Games.FromSoft.Merge;

/// <summary>
/// Three-way merge of two mods' edits to one talk state machine file. Whatever can be settled on a whole group is
/// settled there, which is nearly every group; a group both sides touched needs <see cref="EsdAlignment.Align"/> to
/// pair its states up first, see <see cref="MergeGroup"/>. The base is the preferred side: wherever the two cannot
/// both be kept, the base's version stays and the note says what the other mod lost.
/// </summary>
public static class EsdMerge
{
    /// <summary>Composes <paramref name="other"/>'s edits onto <paramref name="base"/>, both having branched from <paramref name="vanilla"/>.</summary>
    /// <exception cref="EsdMergeException">
    /// The two sides added a group of the same id with different contents, a graft jumps at a state the base deleted,
    /// or the composed graph does not hold together.
    /// </exception>
    public static (EsdFile Merged, IReadOnlyList<EsdNote> Notes) Merge(EsdFile @base, EsdFile other, EsdFile vanilla)
    {
        var b = ById(@base);
        var o = ById(other);
        var v = ById(vanilla);
        var notes = new List<EsdNote>();
        var kept = new List<StateGroup>();

        foreach (var id in b.Keys.Concat(o.Keys).Concat(v.Keys).Distinct().Order())
        {
            b.TryGetValue(id, out var baseGroup);
            o.TryGetValue(id, out var otherGroup);
            v.TryGetValue(id, out var vanillaGroup);
            var otherChanged = !Same(otherGroup, vanillaGroup);
            var baseChanged = !Same(baseGroup, vanillaGroup);
            if (!otherChanged)
            {
                if (baseGroup is not null) kept.Add(baseGroup); // the base's own edit, or untouched vanilla
                continue;
            }

            if (!baseChanged)
            {
                if (otherGroup is not null) kept.Add(Degraft(otherGroup)); // only the other side moved
                continue;
            }

            if (otherGroup is null && baseGroup is null) continue; // both deleted it
            if (baseGroup is not null && Same(baseGroup, otherGroup))
            {
                kept.Add(baseGroup); // the same edit on both sides
                continue;
            }

            if (vanillaGroup is null)
            {
                throw new EsdMergeException(
                    $"state group {id} was added by both mods, with different contents -- there is no vanilla version " +
                    "to read either one as an edit of, so there is no delta to replay");
            }

            if (baseGroup is null)
            {
                notes.Add(new EsdNote(id, null, EsdNoteReason.GroupDeletedByBase));
                continue;
            }

            if (otherGroup is null)
            {
                kept.Add(baseGroup);
                notes.Add(new EsdNote(id, null, EsdNoteReason.GroupDeletedByOther));
                continue;
            }

            var (merged, groupNotes) = MergeGroup(id, baseGroup, otherGroup, vanillaGroup);
            kept.Add(merged);
            notes.AddRange(groupNotes);
        }

        var composed = @base with { Groups = kept.OrderBy(g => g.Order is null).ThenBy(g => g.Order ?? 0).ToList() };
        EsdCheck.Check(composed);
        return (composed, notes);
    }

    private static Dictionary<long, StateGroup> ById(EsdFile file)
    {
        var byId = new Dictionary<long, StateGroup>();
        foreach (var group in file.Groups) byId[group.Id] = group;
        return byId;
    }

    /// <summary>
    /// Whether two groups hold the same machine, ignoring how it was spelled. Comparing raw structure instead reads
    /// groups neither mod touched as modified, because every mod tool rewrites machines it never edited, and a group
    /// that reads as modified on both sides goes to <see cref="MergeGroup"/> to compose deltas nobody authored. A
    /// missing group is never canonicalised: there is nothing to compare it against, and a group only one side has may
    /// hold bytecode the decoder refuses without that being anybody's problem.
    /// </summary>
    private static bool Same(StateGroup? a, StateGroup? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return Shape(a).Equals(Shape(b));
    }

    private static EsdList<CanonicalState> Shape(StateGroup group) => new(group.States.Select(EsdCanonical.Canonical));

    /// <summary>
    /// Every record a group holds with its table position cleared. A position is unique only within the file it was
    /// read from, so a group taken from the other file carries indices that collide with the base's by coincidence of
    /// place, not identity, and the writer refuses exactly that collision. Clearing marks each record as new, so the
    /// writer appends it instead of slotting it back into a position that means something else in the merged file.
    /// </summary>
    private static StateGroup Degraft(StateGroup group) =>
        group with { Order = null, States = group.States.Select(Degraft).ToList() };

    private static State Degraft(State state) => state with
    {
        Order = null,
        Conditions = state.Conditions.Select(Degraft).ToList(),
        Entry = state.Entry.Select(Degraft).ToList(),
        Exit = state.Exit.Select(Degraft).ToList(),
        While = state.While.Select(Degraft).ToList(),
    };

    private static Condition Degraft(Condition condition) => condition with
    {
        Order = null,
        BlobOrder = null,
        PassCommands = condition.PassCommands.Select(Degraft).ToList(),
        Subconditions = condition.Subconditions.Select(Degraft).ToList(),
    };

    private static CommandCall Degraft(CommandCall call) => call with
    {
        Order = null,
        Args = call.Args.Select(a => a with { Order = null, BlobOrder = null }).ToList(),
    };

    /// <summary>A jump target in the other mod's numbering that has no counterpart in the merge.</summary>
    private sealed class Unplaceable(long target) : Exception
    {
        public long Target { get; } = target;
    }

    /// <summary>
    /// Replays the other mod's edits to one group onto the base's version of it. Neither side's state ids can be
    /// trusted as identities here, since one mod deletes states and renumbers the survivors, so everything goes
    /// through an alignment against the vanilla both sides branched from. The other mod's delta is read against
    /// vanilla and written out in the base's numbering.
    /// </summary>
    /// <remarks>
    /// Anything that cannot be replayed is named in the returned notes rather than dropped: a hook that quietly fails
    /// to land leaves a mod installed, loading and doing nothing.
    /// </remarks>
    private static (StateGroup Merged, List<EsdNote> Notes) MergeGroup(
        long groupId, StateGroup baseGroup, StateGroup otherGroup, StateGroup vanillaGroup)
    {
        var toBase = EsdAlignment.Align(vanillaGroup, baseGroup);
        var toOther = EsdAlignment.Align(vanillaGroup, otherGroup);
        var vanillaStates = vanillaGroup.States.ToDictionary(s => s.Id);
        var baseStates = baseGroup.States.ToDictionary(s => s.Id);
        var otherStates = otherGroup.States.ToDictionary(s => s.Id);
        var otherToVanilla = toOther.Pairs.ToDictionary(p => p.Value, p => p.Key);

        var notes = new List<EsdNote>();
        var merged = new Dictionary<long, State>(baseStates);
        var layout = baseGroup.States.Select(s => s.Id).ToList();

        // A state the other mod holds that vanilla never did is an addition to graft. That is RightOnly and not
        // "everything unpaired": an unpaired state may also be one the alignment could not place, and grafting one of
        // those adds a second copy of a machine already in the group. Those get no note under their own id and need
        // none: a state lands there only by looking exactly like an unpaired vanilla state, and that vanilla state is
        // in Unresolved and named below. The note points at the vanilla id because that is the id the user can look
        // up in all three files.
        //
        // Ids are unique only within one file, so a graft keeps its own number only where the base is not already
        // using it, and every reference to it has to move with it. That is why the whole allocation happens before
        // anything is retargeted.
        var additions = toOther.RightOnly;
        var used = baseStates.Keys.ToHashSet();
        var graftId = new Dictionary<long, long>();
        foreach (var sid in additions)
        {
            graftId[sid] = used.Contains(sid) ? used.DefaultIfEmpty(-1).Max() + 1 : sid;
            used.Add(graftId[sid]);
        }

        // An id in the other mod's numbering, as an id in the merged group.
        long? Retarget(long? target)
        {
            if (target is not { } id) return null;
            if (graftId.TryGetValue(id, out var grafted)) return grafted;
            if (otherToVanilla.TryGetValue(id, out var vanillaId) && toBase.Pairs.TryGetValue(vanillaId, out var settled))
                return settled;
            throw new Unplaceable(id);
        }

        // The other mod's state, renumbered into the merged group and detached from the other file's record tables.
        // Commands come across with the conditions: a state's meaning is not only where it branches.
        State Replay(State state, long id, int? order)
        {
            var fresh = Degraft(state);
            return fresh with { Id = id, Order = order, Conditions = RetargetedConditions(fresh.Conditions, Retarget) };
        }

        foreach (var sid in additions)
        {
            try
            {
                merged[graftId[sid]] = Replay(otherStates[sid], graftId[sid], null);
            }
            catch (Unplaceable jump)
            {
                throw new EsdMergeException(
                    $"group {groupId}: the other mod's new state {sid} jumps to its state {jump.Target}, " +
                    "which has no counterpart in the merged group");
            }

            layout.Add(graftId[sid]);
        }

        foreach (var vanillaId in vanillaStates.Keys.Order())
        {
            if (toOther.Unresolved.Contains(vanillaId))
            {
                notes.Add(new EsdNote(groupId, vanillaId, EsdNoteReason.UnresolvedInOther));
                continue;
            }

            if (!toOther.Pairs.TryGetValue(vanillaId, out var otherId))
            {
                // "The preferred mod's copy was kept" is only true if there is one. LeftOnly is the alignment saying
                // the base has nothing this could be, as opposed to Unresolved, where some copy of it does ship.
                notes.Add(new EsdNote(groupId, vanillaId,
                    toBase.LeftOnly.Contains(vanillaId) ? EsdNoteReason.NotInEither : EsdNoteReason.NotInOther));
                continue;
            }

            if (!Edited(vanillaStates[vanillaId], otherStates[otherId], toOther.Pairs)) continue;
            if (toBase.Unresolved.Contains(vanillaId))
            {
                notes.Add(new EsdNote(groupId, vanillaId, EsdNoteReason.UnresolvedInBase));
                continue;
            }

            if (!toBase.Pairs.TryGetValue(vanillaId, out var baseId))
            {
                notes.Add(new EsdNote(groupId, vanillaId, EsdNoteReason.NotInBase));
                continue;
            }

            if (Edited(vanillaStates[vanillaId], baseStates[baseId], toBase.Pairs))
            {
                notes.Add(new EsdNote(groupId, vanillaId, EsdNoteReason.BothChanged));
                continue;
            }

            try
            {
                merged[baseId] = Replay(otherStates[otherId], baseId, baseStates[baseId].Order);
            }
            catch (Unplaceable jump)
            {
                notes.Add(new EsdNote(groupId, vanillaId, EsdNoteReason.UnplaceableJump, jump.Target));
            }
        }

        var composed = baseGroup with { States = layout.Select(id => merged[id]).ToList() };
        foreach (var sid in OrphanedGrafts(otherGroup, composed, graftId))
            notes.Add(new EsdNote(groupId, sid, EsdNoteReason.OrphanedGraft, graftId[sid]));
        return (composed, notes);
    }

    /// <summary>
    /// Whether <paramref name="after"/> is a changed version of <paramref name="before"/>, read through
    /// <paramref name="pairs"/>. A raw comparison is no good: a mod that renumbered its states rewrote every jump
    /// target along with them, so an untouched state stops matching itself. Targets on the vanilla side are
    /// translated into the mod's numbering first, and one the alignment could not place reads as a difference rather
    /// than risking a false match.
    /// </summary>
    private static bool Edited(State before, State after, IReadOnlyDictionary<long, long> pairs)
    {
        Jump Through(long? target) => target is not { } id ? Jump.None
            : pairs.TryGetValue(id, out var mapped) ? new Jump(JumpKind.State, mapped) : Jump.Unsettled;
        Jump AsIs(long? target) => target is { } id ? new Jump(JumpKind.State, id) : Jump.None;
        return !EsdAlignment.Retargeted(EsdCanonical.Canonical(before), Through)
            .Equals(EsdAlignment.Retargeted(EsdCanonical.Canonical(after), AsIs));
    }

    private static List<Condition> RetargetedConditions(IEnumerable<Condition> conditions, Func<long?, long?> retarget) =>
        conditions.Select(c => c with
        {
            Target = retarget(c.Target),
            Subconditions = RetargetedConditions(c.Subconditions, retarget),
        }).ToList();

    /// <summary>
    /// States the other mod could enter and the merged group cannot. Deliberately relative, not "every state is
    /// reachable": shipped files are full of states nothing jumps at, the same in both mods, so an absolute rule
    /// would reject FromSoft's own work. What is not normal is a machine the other mod added and could enter arriving
    /// with nothing left pointing at it, which is what happens when the hook that would have entered it loses a
    /// conflict. The result is a note rather than a refusal: the base's mod still works, and a merge that keeps one
    /// mod and names what the other lost beats aborting and shipping neither.
    /// </summary>
    private static IEnumerable<long> OrphanedGrafts(StateGroup otherGroup, StateGroup mergedGroup, Dictionary<long, long> graftId)
    {
        var live = EsdCheck.Reachable(otherGroup);
        var arrived = EsdCheck.Reachable(mergedGroup);
        return graftId.Keys.Order().Where(sid => live.Contains(sid) && !arrived.Contains(graftId[sid])).ToList();
    }
}
