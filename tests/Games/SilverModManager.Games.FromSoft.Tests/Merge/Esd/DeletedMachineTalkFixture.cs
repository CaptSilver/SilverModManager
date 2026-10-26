using SilverModManager.Games.FromSoft.Formats;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

/// <summary>
/// One talk archive in three versions, shaped like a mod that deletes a machine against a mod that hooks into it. The
/// hooking mod calls machine 20 from machine 10 and also edits machine 20 itself.
/// <list type="bullet">
/// <item><see cref="Vanilla"/>: machine 10 calling nothing, and machine 20.</item>
/// <item><see cref="Deleter"/>: machine 20 removed.</item>
/// <item><see cref="Hooker"/>: machine 10's state 1 calls machine 20, and machine 20 has a state of its own.</item>
/// </list>
/// With the deleter as the base, machine 20 is deleted and the hooker's machine 10 is carried over still calling it,
/// so that order cannot be written. With the hooker as the base its edit to machine 20 outlives the deletion and the
/// merge holds together.
/// </summary>
public static class DeletedMachineTalkFixture
{
    public static readonly byte[] Vanilla = StaleTalkFixture.Talk(StaleTalkFixture.Caller(), StaleTalkFixture.Machine(20));

    public static readonly byte[] Deleter = StaleTalkFixture.Talk(StaleTalkFixture.Caller());

    public static readonly byte[] Hooker = StaleTalkFixture.Talk(
        StaleTalkFixture.Caller(20),
        new StateGroup(20, [StaleTalkFixture.Plain(0), StaleTalkFixture.Plain(5)]));
}
