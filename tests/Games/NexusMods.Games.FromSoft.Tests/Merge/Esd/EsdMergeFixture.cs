using NexusMods.Games.FromSoft.Formats;

namespace NexusMods.Games.FromSoft.Tests.Merge;

/// <summary>
/// Real bytecode in the spellings the real files use. An evaluator is a postfix stack program: 0x40+n pushes a small
/// int, 0x82 pushes an int32, 0x80/0x81 push a float32/float64, 0xB8/0xB9/0xBA are builtins, 0x95/0x96/0x98/0x99 are
/// == != &amp;&amp; ||, and 0xA1 terminates it.
/// </summary>
public static class EsdMergeFixture
{
    /// <summary>Push 1: FromSoft's "IF 1".</summary>
    public static readonly byte[] Always = [0x41, 0xA1];

    /// <summary>The same condition in the four-byte spelling both mod toolchains write.</summary>
    public static readonly byte[] AlwaysInt32 = [0x82, 0x01, 0x00, 0x00, 0x00, 0xA1];

    /// <summary>And a third spelling: 1 == 1.</summary>
    public static readonly byte[] AlwaysEqOne = [0x41, 0x82, 0x01, 0x00, 0x00, 0x00, 0x95, 0xA1];

    /// <summary>b9 != ba.</summary>
    public static readonly byte[] Guard = [0xB9, 0xBA, 0x96, 0xA1];

    /// <summary>b8(15).</summary>
    public static readonly byte[] Other = [0x4F, 0xB8, 0xA1];

    public static readonly byte[] GuardAndOther = [0xB9, 0xBA, 0x96, 0x4F, 0xB8, 0x98, 0xA1];
    public static readonly byte[] GuardOrOther = [0xB9, 0xBA, 0x96, 0x4F, 0xB8, 0x99, 0xA1];
    public static readonly byte[] GuardOrGuard = [0xB9, 0xBA, 0x96, 0xB9, 0xBA, 0x96, 0x99, 0xA1];

    /// <param name="groups">Each group's id and its states as (state id, jump target or null).</param>
    public static EsdFile Archive(params (long GroupId, (long StateId, long? Target)[] States)[] groups) =>
        new(groups.OrderBy(g => g.GroupId).Select(g => new StateGroup(
            g.GroupId,
            g.States.Select(s => new State(s.StateId,
                s.Target is { } t ? [new Condition(t, Evaluator: Always)] : [])).ToList())).ToList(),
            "t000001000", [0u, 0u, 0u, 0u]);

    public static State StateOf(long id, params Condition[] conditions) => new(id, conditions);

    public static Condition Cond(long? target, byte[]? evaluator = null, Condition[]? subs = null, CommandCall[]? passes = null) =>
        new(target, passes, subs, evaluator ?? Guard);

    /// <summary>One state whose entry command takes a single argument.</summary>
    public static State Calling(byte[] blob) =>
        new(0, Entry: [new CommandCall(1, 2, [new CommandArg(blob)])]);
}
