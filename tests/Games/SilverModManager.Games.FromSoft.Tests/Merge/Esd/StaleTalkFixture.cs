using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using EsdFormat = SilverModManager.Games.FromSoft.Formats.Esd;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

/// <summary>
/// One talk archive in four builds, shaped like a mod pair made against an old game build and a game copy that has
/// since been patched. The caller machine 10 enters machine 20 from its state 1.
/// <list type="bullet">
/// <item><see cref="Old"/>: the build both mods were made from.</item>
/// <item><see cref="Patched"/>: <see cref="Old"/> plus two new machines (40, 41) and a caller state that enters one of them.</item>
/// <item><see cref="Remover"/>: removes machine 20 and the call that enters it.</item>
/// <item><see cref="Adder"/>: adds machine 30.</item>
/// </list>
/// Against <see cref="Old"/> the pair merges. Against <see cref="Patched"/> the patch's edit to the caller reads as
/// both mods' edit, the preferred one's state wins, and it still calls the machine the other mod removed.
/// </summary>
public static class StaleTalkFixture
{
    public const string TalkPath = "script/talk/m00.talkesdbnd.dcx";
    public const string EsdName = "t000001000";

    public static FromSoftFormatTraits Traits => EldenRingGame.Definition.Formats;

    internal static CommandCall Call(int machine) => new(EsdFormat.CallBank, machine);

    internal static State Plain(long id, params Condition[] conditions) => new(id, conditions);

    internal static State Entering(long id, params int[] machines) => new(id, Entry: machines.Select(Call).ToList());

    internal static StateGroup Machine(long id) => new(id, [Plain(0)]);

    internal static StateGroup Caller(params int[] enteredBySecondState)
        => new(10, [Plain(0, new Condition(1, Evaluator: EsdMergeFixture.Always)), Entering(1, enteredBySecondState)]);

    internal static byte[] Talk(params StateGroup[] groups)
    {
        var file = new EsdFile(groups.OrderBy(g => g.Id).ToList(), EsdName, [0u, 0u, 0u, 0u]);
        var members = new[] { (Id: 1000, Name: EsdName + ".esd", Data: EsdFormat.Write(file)) };
        var binder = Traits.Binder switch
        {
            BinderFormat.Bnd4 => Bnd4Fixture.Build(members),
            var other => throw new NotSupportedException($"No fixture builds {other} archives."),
        };
        return Dcx.Write(binder, Traits.PackedWrite);
    }

    public static readonly byte[] Old = Talk(Caller(20), Machine(20));

    public static readonly byte[] Patched = Talk(Caller(20, 40), Machine(20), Machine(40), Machine(41));

    public static readonly byte[] Remover = Talk(Caller());

    public static readonly byte[] Adder = Talk(Caller(20), Machine(20), Machine(30));
}
