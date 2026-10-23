using SilverModManager.Games.FromSoft.Formats;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// How one group's states line up with another's. <see cref="Pairs"/> is the answer. The rest is why a state is not
/// in it, and the two reasons differ: <see cref="LeftOnly"/> and <see cref="RightOnly"/> are states with no
/// counterpart left to have (a real delete, a real add), while <see cref="Unresolved"/> is the alignment admitting
/// defeat over candidates it could not tell apart. A caller replaying a delta has to treat those differently, so a
/// state never just goes missing. The right-hand states behind <see cref="Unresolved"/> are the ones in neither
/// <c>Pairs.Values</c> nor <see cref="RightOnly"/>.
/// </summary>
public sealed record Alignment(
    IReadOnlyDictionary<long, long> Pairs,
    IReadOnlyList<long> LeftOnly,
    IReadOnlyList<long> RightOnly,
    IReadOnlyList<long> Unresolved);

public enum JumpKind { None, Unsettled, State }

/// <summary>A jump target as alignment reads it: absent, not yet paired, or a state id on one side.</summary>
public readonly record struct Jump(JumpKind Kind, long State = 0)
{
    public static Jump None => new(JumpKind.None);

    /// <summary>A jump whose far end has not been paired yet.</summary>
    public static Jump Unsettled => new(JumpKind.Unsettled);
}

public sealed record RetargetedCondition(
    Jump Target,
    EsdExpr? Evaluator,
    EsdList<CanonicalCall> Passes,
    EsdList<RetargetedCondition> Subconditions);

/// <summary>A state's canonical form minus its own id, with jump targets rewritten.</summary>
public sealed record RetargetedShape(
    EsdList<RetargetedCondition> Conditions,
    EsdList<CanonicalCall> Entry,
    EsdList<CanonicalCall> Exit,
    EsdList<CanonicalCall> While);

public static class EsdAlignment
{
    /// <summary>
    /// Lines <paramref name="left"/>'s states up with <paramref name="right"/>'s, by content rather than by id.
    /// </summary>
    /// <remarks>
    /// A mod that deletes states from a shared group and renumbers the survivors densely makes the same id mean
    /// different things on the two sides, so matching on it pairs unrelated machines. Content alone is not enough
    /// either, because a state's content includes where its conditions jump, and those are exactly the ids a
    /// renumbering moves. Identity depends on the targets and the targets' identity depends on the mapping, which
    /// makes it a fixed point: seed on the part of a state a renumbering cannot touch, then re-read the jump
    /// targets through whatever is settled and go round again until a round settles nothing new.
    /// Known limit: the shape matched includes a state's commands, so a state whose commands changed cannot pair
    /// with its own original and reads as a delete plus an add.
    /// </remarks>
    public static Alignment Align(StateGroup left, StateGroup right)
    {
        var leftShape = left.States.ToDictionary(s => s.Id, EsdCanonical.Canonical);
        var rightShape = right.States.ToDictionary(s => s.Id, EsdCanonical.Canonical);
        var leftIds = leftShape.Keys.Order().ToList();
        var rightIds = rightShape.Keys.Order().ToList();
        var pairs = new Dictionary<long, long>();
        var taken = new HashSet<long>();

        // Adds every pair this round can justify; answers how many that was.
        int Settle(Func<long?, Jump> fromLeft, Func<long?, Jump> fromRight, bool byId = false)
        {
            var added = 0;
            if (byId)
            {
                foreach (var id in leftIds)
                {
                    if (!pairs.ContainsKey(id) && rightShape.TryGetValue(id, out var theirs) && !taken.Contains(id)
                        && Retargeted(leftShape[id], fromLeft).Equals(Retargeted(theirs, fromRight)))
                    {
                        pairs[id] = id;
                        taken.Add(id);
                        added++;
                    }
                }
            }

            // Only a candidate that is alone on both sides is safe to take: two states with the same content are
            // interchangeable until something downstream of them settles and tells them apart.
            var lefts = new Dictionary<RetargetedShape, List<long>>();
            var rights = new Dictionary<RetargetedShape, List<long>>();
            foreach (var id in leftIds.Where(id => !pairs.ContainsKey(id)))
                Add(lefts, Retargeted(leftShape[id], fromLeft), id);
            foreach (var id in rightIds.Where(id => !taken.Contains(id)))
                Add(rights, Retargeted(rightShape[id], fromRight), id);

            foreach (var (shape, candidates) in lefts)
            {
                if (candidates.Count == 1 && rights.TryGetValue(shape, out var twins) && twins.Count == 1)
                {
                    pairs[candidates[0]] = twins[0];
                    taken.Add(twins[0]);
                    added++;
                }
            }

            return added;
        }

        Settle(TargetMasked, TargetMasked, byId: true);
        Jump ThroughPairs(long? t) => t is { } id ? pairs.TryGetValue(id, out var mapped) ? new Jump(JumpKind.State, mapped) : Jump.Unsettled : Jump.None;
        Jump AlreadyTaken(long? t) => t is { } id ? taken.Contains(id) ? new Jump(JumpKind.State, id) : Jump.Unsettled : Jump.None;
        while (Settle(ThroughPairs, AlreadyTaken) > 0)
        {
        }

        var (leftOnly, rightOnly, unresolved) = Leftovers(leftShape, rightShape, leftIds, rightIds, pairs, taken);
        return new Alignment(pairs, leftOnly, rightOnly, unresolved);
    }

    private static void Add(Dictionary<RetargetedShape, List<long>> into, RetargetedShape shape, long id)
    {
        if (!into.TryGetValue(shape, out var ids)) into[shape] = ids = [];
        ids.Add(id);
    }

    /// <summary>
    /// Splits what went unpaired into "nothing it could have been" and "could not choose", measured against the
    /// states still going spare on the other side.
    /// </summary>
    private static (List<long> LeftOnly, List<long> RightOnly, List<long> Unresolved) Leftovers(
        Dictionary<long, CanonicalState> leftShape, Dictionary<long, CanonicalState> rightShape,
        List<long> leftIds, List<long> rightIds, Dictionary<long, long> pairs, HashSet<long> taken)
    {
        var spareRight = rightIds.Where(id => !taken.Contains(id))
            .Select(id => Retargeted(rightShape[id], TargetMasked)).ToHashSet();
        var spareLeft = leftIds.Where(id => !pairs.ContainsKey(id))
            .Select(id => Retargeted(leftShape[id], TargetMasked)).ToHashSet();

        List<long> leftOnly = [], unresolved = [];
        foreach (var id in leftIds.Where(id => !pairs.ContainsKey(id)))
        {
            var possible = spareRight.Contains(Retargeted(leftShape[id], TargetMasked));
            (possible ? unresolved : leftOnly).Add(id);
        }

        var rightOnly = rightIds
            .Where(id => !taken.Contains(id) && !spareLeft.Contains(Retargeted(rightShape[id], TargetMasked)))
            .ToList();
        return (leftOnly, rightOnly, unresolved);
    }

    /// <summary>
    /// Every jump looks the same: the seed round has no mapping to read them through yet, and their raw ids are
    /// the thing that moved.
    /// </summary>
    public static Jump TargetMasked(long? target) => target is null ? Jump.None : Jump.Unsettled;

    /// <summary>
    /// Dropping the id is what lets a renumbered state match at all; rewriting the targets is what stops the ids
    /// buried inside it doing the same damage.
    /// </summary>
    public static RetargetedShape Retargeted(CanonicalState shape, Func<long?, Jump> translate) =>
        new(Conditions(shape.Conditions, translate), shape.Entry, shape.Exit, shape.While);

    private static EsdList<RetargetedCondition> Conditions(IEnumerable<CanonicalCondition> conditions, Func<long?, Jump> translate) =>
        new(conditions.Select(c => new RetargetedCondition(
            translate(c.Target), c.Evaluator, c.Passes, Conditions(c.Subconditions, translate))));
}
