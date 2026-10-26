using SilverModManager.Games.FromSoft.Formats;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

/// <summary>
/// One talk archive in three versions, shaped like a mod that deletes a machine and drops the call into it against a
/// mod that keeps the call and adds a state next to it. Neither touches machine 20 beyond the deletion, so the pair is
/// not a deleted-against-changed conflict.
/// <list type="bullet">
/// <item><see cref="Vanilla"/>: machine 10 whose state 1 enters machine 20, and machine 20.</item>
/// <item><see cref="Deleter"/>: machine 20 removed, and state 1 enters nothing.</item>
/// <item><see cref="Hooker"/>: state 1 still enters machine 20, and machine 10 has a state 5 of its own.</item>
/// </list>
/// With the hooker as the base its state 1 outlives the deletion and still enters machine 20, so that order cannot be
/// written. With the deleter as the base the hooker's added state is carried over, nothing calls machine 20 and the
/// merge holds together.
/// </summary>
public static class DeletedMachineTalkFixture
{
    public static readonly byte[] Vanilla = StaleTalkFixture.Talk(StaleTalkFixture.Caller(20), StaleTalkFixture.Machine(20));

    public static readonly byte[] Deleter = StaleTalkFixture.Talk(StaleTalkFixture.Caller());

    public static readonly byte[] Hooker = StaleTalkFixture.Talk(
        new StateGroup(10, [.. StaleTalkFixture.Caller(20).States, StaleTalkFixture.Plain(5)]),
        StaleTalkFixture.Machine(20));
}
