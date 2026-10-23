using SilverModManager.Games.FromSoft.Formats;
using EsdFormat = SilverModManager.Games.FromSoft.Formats.Esd;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// Structural invariants for a merged state-machine graph: the ESD analogue of checking that every authored byte
/// survived a param merge. A graft that renumbers wrongly shows up here as a dangling target, not as a dead grace
/// menu hours into a playthrough.
/// </summary>
public static class EsdCheck
{
    /// <exception cref="EsdMergeException">A jump lands on a state the group lacks, a group numbers two states alike, or a bank-6 call names a group the file lacks.</exception>
    public static void Check(EsdFile file)
    {
        var groupIds = file.Groups.Select(g => g.Id).ToHashSet();
        foreach (var group in file.Groups)
        {
            var ids = group.States.Select(s => s.Id).ToList();
            var present = ids.ToHashSet();
            if (ids.Count != present.Count)
            {
                var repeated = present.Where(id => ids.Count(i => i == id) > 1).Order();
                throw new EsdMergeException(
                    $"state group {group.Id} has more than one state numbered {string.Join(", ", repeated)}");
            }

            foreach (var state in group.States)
            {
                var where = $"group {group.Id} state {state.Id}";
                var calls = state.Entry.Concat(state.Exit).Concat(state.While).ToList();
                foreach (var condition in EveryCondition(state.Conditions))
                {
                    if (condition.Target is { } target && !present.Contains(target))
                        throw new EsdMergeException($"{where} jumps to state {target}, which does not exist in that group");
                    calls.AddRange(condition.PassCommands);
                }

                foreach (var call in calls)
                {
                    if (call.Bank == EsdFormat.CallBank && !groupIds.Contains(call.Id))
                        throw new EsdMergeException($"{where} calls machine {call.Id}, which is not in the merged file");
                }
            }
        }
    }

    /// <summary>
    /// State ids reachable from the group's entry, which is whatever the writer points the group header at: the one
    /// rule that decides where the game starts the machine.
    /// </summary>
    public static IReadOnlySet<long> Reachable(StateGroup group)
    {
        var states = group.States.ToDictionary(s => s.Id);
        var start = EsdFormat.FirstState(group).Id;
        var seen = new HashSet<long> { start };
        var pending = new Stack<long>([start]);
        while (pending.Count > 0)
        {
            foreach (var target in JumpTargets(states[pending.Pop()]))
            {
                if (states.ContainsKey(target) && seen.Add(target)) pending.Push(target);
            }
        }

        return seen;
    }

    /// <summary>Every jump a state makes, including the ones nested inside a condition.</summary>
    public static IEnumerable<long> JumpTargets(State state)
    {
        foreach (var condition in EveryCondition(state.Conditions))
        {
            if (condition.Target is { } target) yield return target;
        }
    }

    /// <summary>A condition list and every subcondition below it, depth first.</summary>
    public static IEnumerable<Condition> EveryCondition(IEnumerable<Condition> conditions)
    {
        foreach (var condition in conditions)
        {
            yield return condition;
            foreach (var inner in EveryCondition(condition.Subconditions)) yield return inner;
        }
    }
}
