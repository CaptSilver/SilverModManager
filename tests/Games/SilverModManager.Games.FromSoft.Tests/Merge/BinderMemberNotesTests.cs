using FluentAssertions;
using SilverModManager.Games.FromSoft.Merge;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

public class BinderMemberNotesTests
{
    [Fact]
    public void AKeptMemberNamesBothModsAndTheModelWhoseCopyIsUsed()
    {
        var note = new MemberKept("WP_A_0556.flver", "Weapon Glow", "Moveset", null);

        note.Describe().Should().Be("member WP_A_0556.flver: both mods changed it; kept Weapon Glow's");
        note.Summary().Should().Be("both Weapon Glow and Moveset change the model WP_A_0556.flver; Weapon Glow's is used.");
        note.Loss.Should().Be(new MergeLoss("Moveset", "Weapon Glow"));
    }

    [Fact]
    public void AKeptMemberCarriesTheReadersMessageAsDetails()
        => new MemberKept("A.tpf", "A", "B", "member 3 is cut off").Details.Should().Be("member 3 is cut off");

    [Fact]
    public void AKeptMemberWithGenericNamesHasNoLoss()
        => new MemberKept("A.flver", MergeNames.PreferredMod, MergeNames.OtherMod, null).Loss.Should().BeNull();

    [Fact]
    public void ATakenMemberSaysWhichModChangedItAndWhereItCameFrom()
    {
        IMergeNote note = new MemberTaken("WP_A_0556.anibnd", "Moveset");

        note.Describe().Should().Be("member WP_A_0556.anibnd: only Moveset changed it; taken from there");
        note.Summary().Should().Be(note.Describe());
        note.Loss.Should().BeNull();
        note.Should().BeAssignableTo<IMergeDetailNote>();
    }

    [Theory]
    [InlineData("WP_A_0556.flver", "the model")]
    [InlineData("WP_A_0556.ANIBND", "the animations")]
    [InlineData("N:\\FDP\\data\\Other\\tex\\a.tpf", "the textures")]
    [InlineData("a.hkx", "the file")]
    [InlineData("noextension", "the file")]
    public void TheNounComesFromTheExtensionAfterTheLastBackslash(string member, string noun)
        => MemberPolicy.Noun(member).Should().Be(noun);

    [Theory]
    [InlineData("a.tpf", MemberMerge.Texture)]
    [InlineData("C:\\dir.tpf\\a.TPF", MemberMerge.Texture)]
    [InlineData("a.flver", MemberMerge.Opaque)]
    [InlineData("C:\\dir.tpf\\a.flver", MemberMerge.Opaque)]
    [InlineData("a.anibnd", MemberMerge.Opaque)]
    public void OnlyTpfMembersAreMergedByTexture(string member, MemberMerge expected)
        => MemberPolicy.For(member).Should().Be(expected);

    [Fact]
    public void AKeptMemberRaisesACardAndATakenMemberDoesNot()
    {
        var cards = MergeDiagnosticsEmitter.NoteCards(
            "parts/wp_a_0556.partsbnd.dcx", "Weapon Glow, Moveset",
            [new MemberTaken("WP_A_0556.anibnd", "Moveset"), new MemberKept("WP_A_0556.flver", "Weapon Glow", "Moveset", null)]).ToArray();

        var card = cards.Should().ContainSingle().Which;
        card.Should().BeOfType<SilverModManager.Abstractions.Diagnostics.Diagnostic<FromSoftDiagnostics.MergeNoteMessageData>>()
            .Which.MessageData.Note.Should().Contain("WP_A_0556.flver");
    }

    [Fact]
    public void TheMergeReportListsATakenMember()
        => MergeReports.ReportNotes([new MemberTaken("A.anibnd", "Moveset"), new MemberKept("A.flver", "X", "Y", null), new BaselineWillBeKept("1.0")])
            .Should().Equal("member A.anibnd: only Moveset changed it; taken from there", "member A.flver: both mods changed it; kept X's");
}
