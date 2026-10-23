using SilverModManager.Games.FromSoft.Formats;
using static SilverModManager.Games.FromSoft.Merge.EsdExpressions;

namespace SilverModManager.Games.FromSoft.Merge;

public sealed record CanonicalCall(int Bank, int Id, EsdList<EsdExpr?> Args);

public sealed record CanonicalCondition(
    long? Target,
    EsdExpr? Evaluator,
    EsdList<CanonicalCall> Passes,
    EsdList<CanonicalCondition> Subconditions);

public sealed record CanonicalState(
    long Id,
    EsdList<CanonicalCondition> Conditions,
    EsdList<CanonicalCall> Entry,
    EsdList<CanonicalCall> Exit,
    EsdList<CanonicalCall> While);

/// <summary>
/// A comparable form of a state with the encoders' re-spellings removed. Compare raw structure instead and nearly
/// every group reads as modified, because every mod tool rewrites machines it never edited. What survives here is
/// the state's meaning: which condition jumps where, under which expression, running which commands.
/// </summary>
public static class EsdCanonical
{
    /// <exception cref="EsdMergeException">An evaluator or argument holds bytecode that cannot be decoded.</exception>
    public static CanonicalState Canonical(State state)
    {
        var slots = new Dictionary<int, EsdExpr>();
        return new CanonicalState(
            state.Id,
            Conditions(state.Conditions, slots),
            Calls(state.Entry, slots),
            Calls(state.Exit, slots),
            Calls(state.While, slots));
    }

    private static EsdList<CanonicalCall> Calls(IEnumerable<CommandCall> calls, Dictionary<int, EsdExpr> slots) =>
        new(calls.Select(c => Call(c, slots)));

    private static EsdList<CanonicalCondition> Conditions(IEnumerable<Condition> conditions, Dictionary<int, EsdExpr> slots)
    {
        var folded = new List<CanonicalCondition>();
        foreach (var condition in conditions)
        {
            var item = One(condition, slots);
            if (folded.Count > 0 && folded[^1] is { Evaluator: { } before } last && item.Evaluator is { } after
                && IsOneBranchTwice(last, item))
            {
                folded[^1] = new CanonicalCondition(
                    last.Target, Combine(Or, before, after), EsdList<CanonicalCall>.Empty, EsdList<CanonicalCondition>.Empty);
            }
            else
            {
                folded.Add(item);
            }
        }

        return new EsdList<CanonicalCondition>(folded);
    }

    /// <summary>
    /// Whether two adjacent conditions are one branch written apart: the same jump target and nothing else attached
    /// to either, so `IF A -> T; IF B -> T` and `IF A || B -> T` are the same machine. One toolchain writes them
    /// merged. Both need an evaluator to fold: a condition with none has no expression to disjoin, the same reason
    /// the wrapper collapse refuses a missing evaluator instead of guessing it means "always".
    /// </summary>
    private static bool IsOneBranchTwice(CanonicalCondition first, CanonicalCondition second) =>
        first.Target is not null && first.Target == second.Target
        && first.Evaluator is not null && second.Evaluator is not null
        && first.Passes.Count == 0 && first.Subconditions.Count == 0
        && second.Passes.Count == 0 && second.Subconditions.Count == 0;

    private static CanonicalCondition One(Condition condition, Dictionary<int, EsdExpr> slots)
    {
        var evaluator = condition.Evaluator.Length > 0 ? Decode(condition.Evaluator, slots) : null;
        var passes = Calls(condition.PassCommands, slots);
        var subconditions = Conditions(condition.Subconditions, slots);
        var target = condition.Target;

        // `IF E: { IF F -> T }` is `IF E && F -> T`, and vanilla writes the nested form where both mods write the
        // flat one. Only sound while the outer condition runs no commands: those fire when E holds and F does not,
        // which the flattened form would never do.
        if (target is null && passes.Count == 0 && subconditions.Count == 1)
        {
            var child = subconditions[0];
            if (child.Subconditions.Count == 0 && evaluator is not null && child.Evaluator is not null)
            {
                return new CanonicalCondition(
                    child.Target, Combine(And, evaluator, child.Evaluator), child.Passes, EsdList<CanonicalCondition>.Empty);
            }
        }

        return new CanonicalCondition(target, evaluator, passes, subconditions);
    }

    private static CanonicalCall Call(CommandCall call, Dictionary<int, EsdExpr> slots) =>
        new(call.Bank, call.Id,
            new EsdList<EsdExpr?>(call.Args.Select(a => a.Bytecode.Length > 0 ? Decode(a.Bytecode, slots) : null)));
}
