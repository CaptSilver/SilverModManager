using FluentAssertions;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Merge;
using NexusMods.Games.FromSoft.Tests.Formats.Fixtures;
using EsdFormat = NexusMods.Games.FromSoft.Formats.Esd;

namespace NexusMods.Games.FromSoft.Tests.Merge;

/// <summary>
/// Canonical form and alignment over the t000001000 state machine as three real files spell it: the game's own,
/// and the two mods that edit it. Never part of the default run: it needs a checkout of the erm tool and the
/// archives it pinned, and no mod or game file is checked in.
/// </summary>
public class EsdMergeRealArchiveTests
{
    private const string TalkBinder = "script/talk/m00_00_00_00.talkesdbnd.dcx";
    private const string BossRes = "boss-resurrection-lite";
    private const string Melina = "journey-with-melina";

    // The machine's group both mods edit, and one that none of them touch.
    private const long SharedEditedGroup = 2147483624;
    private const long UntouchedGroup = 2147483573;

    private static EsdFile Real(string modId, string member)
    {
        var traits = EldenRingGame.Definition.Formats;
        var container = ErmVendorArchive.ReadPinnedMember(modId, member);
        var entries = Binders.For(traits).Read(Dcx.Read(container, traits));
        return EsdFormat.Read(entries.Single(e => e.Id == 0).Data);
    }

    private static EsdFile Vanilla() => Real("item-enemy-randomizer", "randomizer/diste/Vanilla/m00_00_00_00.talkesdbnd.dcx");

    private static Dictionary<long, StateGroup> ById(EsdFile file) => file.Groups.ToDictionary(g => g.Id);

    private static IReadOnlyList<CanonicalState> Shape(StateGroup group) => group.States.Select(EsdCanonical.Canonical).ToList();

    private static bool SameShape(StateGroup a, StateGroup b) => Shape(a).SequenceEqual(Shape(b));

    [Fact]
    public void FindsExactlyTheGroupsTheModsEdited()
    {
        // Any group where both mods agree with each other is a group neither edited, so it must also match vanilla.
        // Residual noise has to be zero or the canonical form invents differences, and a merge built on it would
        // invent conflicts on machines nobody touched.
        var vanilla = ById(Vanilla());
        var bossRes = ById(Real(BossRes, TalkBinder));
        var melina = ById(Real(Melina, TalkBinder));

        var shared = vanilla.Keys.Intersect(bossRes.Keys).Intersect(melina.Keys).ToList();
        var agree = shared.Where(id => SameShape(bossRes[id], melina[id])).ToList();
        agree.Where(id => !SameShape(bossRes[id], vanilla[id])).Should().BeEmpty("neither mod edited these");
        shared.Should().HaveCount(85);
        agree.Should().HaveCount(83);

        vanilla.Keys.Intersect(bossRes.Keys).Where(id => !SameShape(bossRes[id], vanilla[id]))
            .Should().BeEquivalentTo([2147483616L, 2147483624L]);
        vanilla.Keys.Intersect(melina.Keys).Where(id => !SameShape(melina[id], vanilla[id]))
            .Should().BeEquivalentTo([SharedEditedGroup]);
        bossRes.Keys.Except(vanilla.Keys).Should().BeEquivalentTo(
            Enumerable.Range(2147483498, 10).Concat(Enumerable.Range(2147483513, 5)).Concat(Enumerable.Range(2147483522, 6))
                .Concat(Enumerable.Range(2147483530, 27)).Concat(Enumerable.Range(2147483558, 3)).Select(i => (long)i));
        melina.Keys.Except(vanilla.Keys).Should().BeEquivalentTo([2147482648L]);
        vanilla.Keys.Except(bossRes.Keys).Should().BeEquivalentTo([2147483563L]);
        vanilla.Keys.Except(melina.Keys).Should().BeEmpty();
    }

    [Fact]
    public void PairsEveryStateOfAGroupBothModsLeftAlone()
    {
        // Same machine, three encoders: every state has to find its counterpart or a delta replay has nothing to
        // replay onto.
        var vanilla = ById(Vanilla())[UntouchedGroup];
        var bossRes = ById(Real(BossRes, TalkBinder))[UntouchedGroup];
        var aligned = EsdAlignment.Align(vanilla, bossRes);
        aligned.Pairs.Should().Equal(vanilla.States.ToDictionary(s => s.Id, s => s.Id));
        aligned.LeftOnly.Should().BeEmpty();
        aligned.RightOnly.Should().BeEmpty();
        aligned.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public void FollowsARenumberingThroughTheJumpTargets()
    {
        // Boss Res deletes states from the shared group and renumbers the survivors down, so a state's content
        // stops matching the moment it jumps at one of them. The targets have to be read through the mapping, which
        // makes resolving it a fixed point.
        var vanilla = ById(Vanilla())[SharedEditedGroup];
        var bossRes = ById(Real(BossRes, TalkBinder))[SharedEditedGroup];
        var aligned = EsdAlignment.Align(vanilla, bossRes);

        aligned.Pairs[3].Should().Be(3);
        aligned.Pairs[1].Should().Be(1);
        // Every pair is an identity or the small downward shift a dense renumbering produces.
        aligned.Pairs.Select(p => p.Key - p.Value).Distinct().Should().BeSubsetOf([0L, 1L, 2L, 3L]);
        aligned.Pairs[29].Should().Be(27);
        aligned.Pairs[42].Should().Be(39);

        // Nothing goes missing quietly: every state is accounted for on both sides.
        aligned.Pairs.Keys.Concat(aligned.LeftOnly).Concat(aligned.Unresolved).ToHashSet()
            .Should().BeEquivalentTo(vanilla.States.Select(s => s.Id));
        aligned.Pairs.Values.Concat(aligned.RightOnly).Should().BeSubsetOf(bossRes.States.Select(s => s.Id));
    }

    [Fact]
    public void CheckAcceptsTheThreeRealFiles()
    {
        // The invariants have to be ones a shipped file already satisfies, or they are this module's opinion and
        // not invariants.
        EsdCheck.Check(Vanilla());
        EsdCheck.Check(Real(BossRes, TalkBinder));
        EsdCheck.Check(Real(Melina, TalkBinder));
    }

    [Fact]
    public void TheRealSharedGroupReplaysMelinaOntoBossRes()
    {
        // The one group both mods change, and the whole reason the merge exists. Boss Res deletes vanilla 26, 27 and
        // 41 and renumbers the survivors down; Melina hooks vanilla 26 and 29 into two states she adds. One of her
        // hooks lands on a state Boss Res deleted and is named; the other survives, and with it the path into her
        // machine.
        var (merged, notes) = EsdMerge.Merge(Real(BossRes, TalkBinder), Real(Melina, TalkBinder), Vanilla());
        var group = ById(merged)[SharedEditedGroup];

        // Boss Res's survivors, plus Melina's two additions at their own ids.
        group.States.Select(s => s.Id).Order().Should().Equal(Enumerable.Range(0, 40).Select(i => (long)i).Concat([43L, 44L]));

        // Vanilla 29 is Boss Res's 27, so her redirect had to move with the renumbering.
        Targets(group.States.Single(s => s.Id == 27)).Should().Equal(43L);
        Targets(group.States.Single(s => s.Id == 43)).Should().Equal(44L);
        Targets(group.States.Single(s => s.Id == 44)).Should().Equal(15L);
        group.States.Single(s => s.Id == 44).Entry.Select(c => (c.Bank, c.Id)).Should().Equal((6, 2147482648));

        // The machine she calls came across with her, so the call resolves.
        merged.Groups.Select(g => g.Id).Should().Contain(2147482648L);
        // Her machine is still entered: nothing above is worth anything if the surviving hook sits on an orphan.
        EsdCheck.Reachable(group).Should().Contain([27L, 43L, 44L]);

        // Her other hook was on vanilla 26, which Boss Res deleted. That is the only thing wrong with the merge, and
        // neither state she added arrives orphaned.
        notes.Should().ContainSingle().Which.Should().Be(new EsdNote(SharedEditedGroup, 26, EsdNoteReason.NotInBase));
    }

    private static IReadOnlyList<long> Targets(State state) => EsdMergeFixture.Targets(state);
}
