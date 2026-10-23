using FluentAssertions;
using SilverModManager.Games.FromSoft.Merge;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

public class EsdNoteTests
{
    // What each sentence has to say, as (phrases it must contain, phrases it must not). The notes are what turns
    // "go check the mod still works" into "go check state 26 of group 2147483624", so a reason rendering as the wrong
    // sentence sends someone to the wrong mod's diff. Not the full prose, so rewording stays free; just the words
    // that make one reason a different statement from its neighbours.
    private static readonly Dictionary<EsdNoteReason, (string[] Must, string[] MustNot)> MustSay = new()
    {
        [EsdNoteReason.UnresolvedInOther] = (["several", "other mod's states", "not applied"], ["preferred"]),
        [EsdNoteReason.NotInOther] = (["the other mod", "no state matching", "copy was kept"], ["several"]),
        [EsdNoteReason.NotInEither] = (["neither mod", "state matching"], ["several", "was kept"]),
        [EsdNoteReason.UnresolvedInBase] = (["several", "preferred mod's states", "not applied"], []),
        [EsdNoteReason.NotInBase] = (["preferred mod deleted", "not applied"], ["several"]),
        [EsdNoteReason.BothChanged] = (["both mods changed", "kept the preferred mod's"], ["not applied"]),
        [EsdNoteReason.UnplaceableJump] = (["jumps to", "no counterpart", "not applied"], ["several"]),
        [EsdNoteReason.OrphanedGraft] = (["nothing", "reaches it", "will not run"], ["not applied"]),
    };

    // The two reasons about a whole group name no state.
    private static readonly Dictionary<EsdNoteReason, (string[] Must, string[] MustNot)> MustSayOfAGroup = new()
    {
        [EsdNoteReason.GroupDeletedByBase] = (["preferred mod deleted", "other mod changed", "deletion was kept"], ["several"]),
        [EsdNoteReason.GroupDeletedByOther] = (["other mod deleted", "preferred mod changed", "preferred mod's version was kept"], ["several"]),
    };

    [Fact]
    public void EveryReasonSaysWhatItMeans()
    {
        // A reason with no sentence would throw the first time an apply hit it, and a wrong one is worse: it is
        // confidently misleading.
        MustSay.Keys.Concat(MustSayOfAGroup.Keys).Should().BeEquivalentTo(Enum.GetValues<EsdNoteReason>());
        foreach (var (reason, (must, mustNot)) in MustSay)
        {
            var text = new EsdNote(2147483624, 26, reason, 44).Describe();
            text.Should().Contain("2147483624").And.Contain("26", $"{reason}: {text}");
            foreach (var phrase in must) text.Should().Contain(phrase, $"{reason}: {text}");
            foreach (var phrase in mustNot) text.Should().NotContain(phrase, $"{reason}: {text}");
        }

        foreach (var (reason, (must, mustNot)) in MustSayOfAGroup)
        {
            var text = new EsdNote(2147483624, null, reason).Describe();
            text.Should().Contain("2147483624");
            foreach (var phrase in must) text.Should().Contain(phrase, $"{reason}: {text}");
            foreach (var phrase in mustNot) text.Should().NotContain(phrase, $"{reason}: {text}");
        }
    }

    [Fact]
    public void NoTwoReasonsRenderToTheSameSentence()
    {
        var rendered = Enum.GetValues<EsdNoteReason>().Select(r => new EsdNote(24, 1, r, 2).Describe()).ToList();
        rendered.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void DescribeNamesTheStateAGraftLandedAt()
    {
        // OrphanedGraft is the one reason whose State is the other mod's own id rather than a vanilla one, and both
        // numbers matter: one identifies the mod's state, the other says where to look for it in the merged file.
        var text = new EsdNote(2147483624, 43, EsdNoteReason.OrphanedGraft, 41).Describe();
        text.Should().Contain("43").And.Contain("41");
    }

    [Fact]
    public void ANoteIsAMergeNoteSoOneListCarriesEveryKind()
    {
        IMergeNote note = new EsdNote(24, 1, EsdNoteReason.BothChanged);
        note.Describe().Should().Contain("24");
    }

    [Fact]
    public void ANoteThatNeedsAStateRefusesToRenderWithoutOne()
    {
        var act = () => new EsdNote(24, null, EsdNoteReason.NotInBase).Describe();
        act.Should().Throw<InvalidOperationException>();
    }
}
