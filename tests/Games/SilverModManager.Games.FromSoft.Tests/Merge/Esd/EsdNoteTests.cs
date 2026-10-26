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

    [Fact]
    public void EveryReasonSaysWhatItMeans()
    {
        // A reason with no sentence would throw the first time an apply hit it, and a wrong one is worse: it is
        // confidently misleading.
        MustSay.Keys.Should().BeEquivalentTo(Enum.GetValues<EsdNoteReason>());
        foreach (var (reason, (must, mustNot)) in MustSay)
        {
            var text = new EsdNote(2147483624, 26, reason, 44).Describe();
            text.Should().Contain("2147483624").And.Contain("26", $"{reason}: {text}");
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

    private static EsdNote Named(EsdNoteReason reason)
        => new(2147483624, 26, reason, 44, PreferredMod: "Boss Resurrection - Lite", OtherMod: "Journey With Melina V1.1");

    [Fact]
    public void ASummaryNamesBothModsAndLeavesTheInternalIdsToTheDetails()
    {
        foreach (var reason in Enum.GetValues<EsdNoteReason>())
        {
            var note = Named(reason);
            var summary = note.Summary();
            summary.Should().Contain("Journey With Melina V1.1", $"{reason}: {summary}");
            summary.Should().NotContain("the other mod").And.NotContain("the preferred mod").And.NotContain("2147483624").And.NotContain("vanilla state");
            note.Details.Should().Contain("2147483624").And.Contain("26");
        }
    }

    [Fact]
    public void ADeletedStateTheOtherModChangedSaysWhoseChangeIsNotUsed()
    {
        var note = Named(EsdNoteReason.NotInBase);

        note.Summary().Should().Be(
            "Boss Resurrection - Lite removed a dialogue state that Journey With Melina V1.1 changed, so Journey With Melina V1.1's change to it is not used.");
        note.Loss.Should().Be(new MergeLoss(DroppedMod: "Journey With Melina V1.1", KeptMod: "Boss Resurrection - Lite"));
    }

    [Theory]
    [InlineData(EsdNoteReason.NotInEither)]
    public void ANoteThatDroppedNothingHasNoLoss(EsdNoteReason reason)
        => Named(reason).Loss.Should().BeNull();

    [Fact]
    public void ANoteWithoutRealModNamesHasNoLossToAdviseOn()
        => new EsdNote(24, 1, EsdNoteReason.NotInBase).Loss.Should().BeNull("there is no mod to move below another");
}
