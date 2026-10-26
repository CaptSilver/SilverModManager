using FluentAssertions;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using static SilverModManager.Games.FromSoft.Tests.Merge.StaleTalkFixture;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

/// <summary>Choosing, among several vanilla copies of one packed file, the one the contributors' copies differ from least.</summary>
public class PackedVanillaClosenessTests
{
    private const string Msg = "msg/engus/item.msgbnd.dcx";
    private const string Tex = "menu/hi/01_common.tpf.dcx";

    private static byte[] Messages(params (int Id, string Text)[] texts)
        => MsgbndFixture.Build(new Dictionary<int, IReadOnlyDictionary<int, string?>>
        {
            [1] = texts.ToDictionary(t => t.Id, t => (string?)t.Text),
        }, Traits);

    private static byte[] Textures(params (string Name, string Data)[] textures)
        => Dcx.Write(Tpf.Write(new TpfArchive(
            [.. textures.Select(t => new Texture(t.Name, System.Text.Encoding.ASCII.GetBytes(t.Data), 102, 0, 1, 0))],
            Platform: 0, Flag2: 3, Encoding: 1)), Traits.PackedWrite);

    [Fact]
    public void TheTalkCopyTheModsWereMadeFromIsNearerThanThePatchedOne()
    {
        var pick = VanillaCloseness.Closest([Patched, Old], [Remover, Adder], TalkPath, Traits);

        pick.Index.Should().Be(1);
        // Remover differs from Old in machines 10 and 20, Adder in machine 30.
        pick.Distance.Should().Be(3);
    }

    [Fact]
    public void ThePatchedTalkCopyIsFartherBecauseItsNewMachinesCountAgainstEveryMod()
    {
        var patched = VanillaCloseness.Closest([Patched], [Remover, Adder], TalkPath, Traits).Distance;

        patched.Should().BeGreaterThan(3, "machines 40 and 41 are missing from both mods");
    }

    [Fact]
    public void AnEqualDistanceKeepsTheFirstCandidate()
    {
        VanillaCloseness.Closest([Old, Old.ToArray()], [Remover], TalkPath, Traits).Index.Should().Be(0);
    }

    [Fact]
    public void ACandidateThatCannotBeReadLosesToOneThatCan()
    {
        VanillaCloseness.Closest([new byte[] { 1, 2, 3 }, Old], [Remover, Adder], TalkPath, Traits).Index.Should().Be(1);
    }

    [Fact]
    public void ContributorsThatCannotBeReadMeasureNothingAndLeaveTheFirstCandidate()
    {
        var pick = VanillaCloseness.Closest([Patched, Old], [new byte[] { 9 }, new byte[] { 8 }], TalkPath, Traits);

        pick.Index.Should().Be(0);
        pick.Distance.Should().BeNull();
    }

    [Fact]
    public void APathWithNoStrategyMeasuresNothing()
    {
        VanillaCloseness.Closest([Patched, Old], [Remover], "sfx/other.bin", Traits).Distance.Should().BeNull();
    }

    [Fact]
    public void MessageCopiesAreComparedByTheEntriesThatDiffer()
    {
        var far = Messages((1, "a"), (2, "b"), (3, "c"));
        var near = Messages((1, "a"), (2, "B"));
        var mod = Messages((1, "a"), (2, "B"), (3, "mod"));

        var pick = VanillaCloseness.Closest([far, near], [mod], Msg, Traits);

        pick.Index.Should().Be(1);
        pick.Distance.Should().Be(1, "the mod adds text 3 to the nearer copy");
    }

    [Fact]
    public void TextureCopiesAreComparedByTheTexturesThatDiffer()
    {
        var far = Textures(("a", "1"), ("b", "2"), ("c", "3"));
        var near = Textures(("a", "1"), ("b", "2"));
        var mod = Textures(("a", "1"), ("b", "X"));

        var pick = VanillaCloseness.Closest([far, near], [mod], Tex, Traits);

        pick.Index.Should().Be(1);
        pick.Distance.Should().Be(1);
    }

    private const string Part = "parts/wp_a_0556.partsbnd.dcx";

    private static byte[] Bundle(params (int Id, string Name, byte[] Data)[] members) => PartsbndFixture.Build(Traits, members);

    private static byte[] Blob(byte value) => [value, value, value];

    [Fact]
    public void BundleCopiesAreComparedByTheMembersThatDiffer()
    {
        var far = Bundle((1, "a.flver", Blob(1)), (2, "a.anibnd", Blob(2)), (3, "a.tpf", Blob(3)));
        var near = Bundle((1, "a.flver", Blob(1)), (2, "a.anibnd", Blob(2)), (3, "a.tpf", Blob(4)));
        var mod = Bundle((1, "a.flver", Blob(1)), (2, "a.anibnd", Blob(2)), (3, "a.tpf", Blob(4)));

        var pick = VanillaCloseness.Closest([far, near], [mod], Part, Traits);

        pick.Index.Should().Be(1);
        pick.Distance.Should().Be(0);
    }

    [Fact]
    public void ABundleCandidateThatRepeatsAMemberIdDoesNotCrashTheMeasurement()
    {
        var repeated = Bundle((1, "a.flver", Blob(9)), (1, "b.flver", Blob(8)));
        var good = Bundle((1, "a.flver", Blob(1)));
        var mod = Bundle((1, "a.flver", Blob(1)));

        VanillaCloseness.Closest([repeated, good], [mod], Part, Traits).Index.Should().Be(1);
        VanillaCloseness.Closest([repeated], [mod], Part, Traits).Distance.Should().NotBeNull();
    }
}
