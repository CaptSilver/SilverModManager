using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

/// <summary>Every rule runs for a BND4 game and a BND3 game: the DCX wrapper comes from the game's traits.</summary>
public class TpfMergeTests
{
    public static TheoryData<string> Games => ["Elden Ring", "Test Souls"];

    private static FromSoftFormatTraits TraitsOf(string game) => game switch
    {
        "Elden Ring" => EldenRingGame.Definition.Formats,
        _ => TestSoulsGame.Definition.Formats,
    };

    private static byte[] Blob(FromSoftFormatTraits traits, params (string Name, string Data)[] textures) =>
        Dcx.Write(Tpf.Write(new TpfArchive(
            [.. textures.Select(t => new Texture(t.Name, System.Text.Encoding.ASCII.GetBytes(t.Data), 102, 0, 1, 0))],
            Platform: 0, Flag2: 3, Encoding: 1)), traits.PackedWrite);

    private static Dictionary<string, string> Names(byte[] blob, FromSoftFormatTraits traits) =>
        Tpf.Read(Dcx.Read(blob, traits)).Textures.ToDictionary(t => t.Name, t => System.Text.Encoding.ASCII.GetString(t.Data));

    [Theory, MemberData(nameof(Games))]
    public void UnionAddsTheOtherSidesNewTextures(string game)
    {
        var t = TraitsOf(game);
        var @base = Blob(t, ("SB_Icon_02_A", "clevers-icons"));
        var other = Blob(t, ("SB_Icon_02_A", "vanilla-icons"), ("SB_Status_GRO_00", "rune-status"));
        Names(TpfMerge.Union(@base, other, t), t).Should().Equal(
            new Dictionary<string, string> { ["SB_Icon_02_A"] = "clevers-icons", ["SB_Status_GRO_00"] = "rune-status" });
    }

    [Theory, MemberData(nameof(Games))]
    public void UnionKeepsTheBaseCopyOfASharedTexture(string game)
    {
        var t = TraitsOf(game);
        var @base = Blob(t, ("SB_Icon_02_A", "clevers-with-weapon-icons"));
        var other = Blob(t, ("SB_Icon_02_A", "plain-vanilla"));
        Names(TpfMerge.Union(@base, other, t), t).Should().Equal(
            new Dictionary<string, string> { ["SB_Icon_02_A"] = "clevers-with-weapon-icons" });
    }

    [Theory, MemberData(nameof(Games))]
    public void UnionReturnsTheBaseBytesWhenNothingIsNew(string game)
    {
        var t = TraitsOf(game);
        var @base = Blob(t, ("A", "aa"), ("B", "bb"));
        var other = Blob(t, ("B", "xx"));
        TpfMerge.Union(@base, other, t).Should().Equal(@base);
    }

    [Theory, MemberData(nameof(Games))]
    public void UnionPreservesTheOrderAndFlagsOfEveryTexture(string game)
    {
        // The game reads textures by offset, and per-texture format and mipmap flags are not interchangeable between
        // an atlas and an icon sheet.
        var t = TraitsOf(game);
        var @base = Dcx.Write(Tpf.Write(new TpfArchive(
            [new Texture("A", "aaaa"u8.ToArray(), 102, 0, 3, 1), new Texture("B", "bb"u8.ToArray(), 0, 0, 1, 0)],
            Platform: 0, Flag2: 3, Encoding: 1)), t.PackedWrite);
        var other = Blob(t, ("C", "cc"));

        var merged = Tpf.Read(Dcx.Read(TpfMerge.Union(@base, other, t), t)).Textures;
        merged.Select(x => x.Name).Should().Equal("A", "B", "C");
        (merged[0].Format, merged[0].Mipmaps, merged[0].Flags1).Should().Be((102, 3, 1));
        (merged[1].Format, merged[1].Mipmaps, merged[1].Flags1).Should().Be((0, 1, 0));
    }

    [Theory, MemberData(nameof(Games))]
    public void UnionMatchesErmsUnionOfTheSameArchives(string game)
    {
        // A shared name, two new ones and non-default flags on the base side; erm's output was decompressed before it
        // was stored, so this compares the archive itself and not either side's deflate stream.
        var t = TraitsOf(game);
        var @base = Dcx.Write(Tpf.Write(new TpfArchive(
            [new Texture("A", "aaaa"u8.ToArray(), 102, 0, 3, 1), new Texture("B", "bb"u8.ToArray(), 0, 0, 1, 0)],
            Platform: 0, Flag2: 3, Encoding: 1)), t.PackedWrite);
        var other = Blob(t, ("B", "xx"), ("C", "cc"), ("D", "dddddd"));

        Dcx.Read(TpfMerge.Union(@base, other, t), t).Should().Equal(GoldenFilesTests.Read("tpf_union.tpf"));
    }

    [Theory, MemberData(nameof(Games))]
    public void UnionNamesTheKeptModForASharedTextureWithDifferingBytes(string game)
    {
        var t = TraitsOf(game);
        var @base = Blob(t, ("A", "base-copy"), ("Same", "ss"));
        var other = Blob(t, ("A", "other-copy"), ("Same", "ss"), ("New", "nn"));
        var notes = new List<IMergeNote>();

        var merged = TpfMerge.Union(@base, other, t, notes, "Mod A", "Mod B");

        notes.Should().Equal(new TextureKept("A", "Mod A", "Mod B"));
        merged.Should().Equal(TpfMerge.Union(@base, other, t));
    }

    [Theory, MemberData(nameof(Games))]
    public void UnionNotesTheLossEvenWhenTheOtherSideAddsNothing(string game)
    {
        var t = TraitsOf(game);
        var @base = Blob(t, ("A", "base-copy"));
        var other = Blob(t, ("A", "other-copy"));
        var notes = new List<IMergeNote>();
        TpfMerge.Union(@base, other, t, notes).Should().Equal(@base);
        notes.Should().Equal(new TextureKept("A", "the preferred mod", "the other mod"));
    }

    [Theory, MemberData(nameof(Games))]
    public void UnionNotesNothingForIdenticalSharedTextures(string game)
    {
        var t = TraitsOf(game);
        var notes = new List<IMergeNote>();
        TpfMerge.Union(Blob(t, ("A", "aa")), Blob(t, ("A", "aa")), t, notes);
        notes.Should().BeEmpty();
    }

    [Theory, MemberData(nameof(Games))]
    public void UnionNotesALossWhenBaseShipsANameTwice(string game)
    {
        var t = TraitsOf(game);
        var @base = Blob(t, ("A", "first"), ("A", "second"));
        var other = Blob(t, ("A", "other-copy"));
        var notes = new List<IMergeNote>();
        TpfMerge.Union(@base, other, t, notes).Should().Equal(@base);
        notes.Should().Equal(new TextureKept("A", "the preferred mod", "the other mod"));
    }

    [Fact]
    public void ATextureNoteNamesBothModsAndWhichCopyIsUsed()
    {
        var note = new TextureKept("SB_Icon_02_A", "Great Rune Overhaul", "Rune Icons");

        note.Summary().Should().Be("both Great Rune Overhaul and Rune Icons replace the texture SB_Icon_02_A; Great Rune Overhaul's is used.");
        note.Loss.Should().Be(new MergeLoss("Rune Icons", "Great Rune Overhaul"));
    }

    [Fact]
    public void ATextureNoteWithoutModNamesHasNoLossToAdviseOn()
        => new TextureKept("A", MergeNames.PreferredMod, MergeNames.OtherMod).Loss.Should().BeNull();

    public static TheoryData<byte> NameEncodings => [0, 1];

    private static Texture Tex(string name, string data, byte format = 102, byte type = 0, byte mipmaps = 1, byte flags1 = 0) =>
        new(name, System.Text.Encoding.ASCII.GetBytes(data), format, type, mipmaps, flags1);

    private static byte[] Raw(byte encoding, params Texture[] textures) =>
        Tpf.Write(new TpfArchive(textures, Platform: 0, Flag2: 3, Encoding: encoding));

    private static List<Texture> Merged(byte[] raw) => [.. Tpf.Read(raw).Textures];

    private static string[] Described(IEnumerable<Texture> textures) =>
        [.. textures.Select(t => $"{t.Name}={System.Text.Encoding.ASCII.GetString(t.Data)}")];

    [Theory, MemberData(nameof(NameEncodings))]
    public void ThreeWayTakesATextureOnlyTheOtherSideChanged(byte e)
    {
        var vanilla = Raw(e, Tex("A", "va"), Tex("B", "vb"));
        var @base = Raw(e, Tex("A", "va"), Tex("B", "vb"));
        var other = Raw(e, Tex("A", "oa"), Tex("B", "vb"));
        var notes = new List<IMergeNote>();

        Described(Merged(TpfMerge.ThreeWay(@base, other, vanilla, notes))).Should().Equal("A=oa", "B=vb");
        notes.Should().BeEmpty();
    }

    [Theory, MemberData(nameof(NameEncodings))]
    public void ThreeWayKeepsBasesTextureWhenOnlyBaseChanged(byte e)
    {
        var vanilla = Raw(e, Tex("A", "va"));
        var @base = Raw(e, Tex("A", "ba"));
        var other = Raw(e, Tex("A", "va"));
        TpfMerge.ThreeWay(@base, other, vanilla).Should().Equal(@base);
    }

    [Theory, MemberData(nameof(NameEncodings))]
    public void ThreeWayKeepsBaseAndNamesTheModsWhenBothChangedATexture(byte e)
    {
        var vanilla = Raw(e, Tex("A", "va"), Tex("B", "vb"));
        var @base = Raw(e, Tex("A", "ba"), Tex("B", "vb"));
        var other = Raw(e, Tex("A", "oa"), Tex("B", "ob"));
        var notes = new List<IMergeNote>();

        var merged = TpfMerge.ThreeWay(@base, other, vanilla, notes, "Mod A", "Mod B");

        Described(Merged(merged)).Should().Equal("A=ba", "B=ob");
        notes.Should().Equal(new TextureKept("A", "Mod A", "Mod B"));
    }

    [Theory, MemberData(nameof(NameEncodings))]
    public void ThreeWayCreditsTheKeptModNamedByTheCaller(byte e)
    {
        var vanilla = Raw(e, Tex("A", "va"));
        var notes = new List<IMergeNote>();
        TpfMerge.ThreeWay(Raw(e, Tex("A", "ba")), Raw(e, Tex("A", "oa")), vanilla, notes, "Mod A", "Mod C", keptName: "Mod B");
        notes.Should().Equal(new TextureKept("A", "Mod B", "Mod C"));
    }

    [Theory, MemberData(nameof(NameEncodings))]
    public void ThreeWayReturnsBaseBytesUntouchedWhenNothingIsTaken(byte e)
    {
        var vanilla = Raw(e, Tex("A", "va"), Tex("B", "vb"));
        var @base = Raw(e, Tex("A", "ba"), Tex("B", "vb"));
        var other = Raw(e, Tex("A", "oa"), Tex("B", "vb"));
        TpfMerge.ThreeWay(@base, other, vanilla).Should().Equal(@base);
    }

    [Theory, MemberData(nameof(NameEncodings))]
    public void ThreeWayAddsATextureOnlyTheOtherSideAddedAfterBasesTextures(byte e)
    {
        var vanilla = Raw(e, Tex("A", "va"));
        var @base = Raw(e, Tex("A", "va"), Tex("BaseNew", "bn"));
        var other = Raw(e, Tex("OtherNew2", "o2"), Tex("A", "va"), Tex("OtherNew1", "o1"));

        Described(Merged(TpfMerge.ThreeWay(@base, other, vanilla))).Should().Equal(
            "A=va", "BaseNew=bn", "OtherNew2=o2", "OtherNew1=o1");
    }

    [Theory, MemberData(nameof(NameEncodings))]
    public void ThreeWayRemovesATextureOnlyTheOtherSideRemoved(byte e)
    {
        var vanilla = Raw(e, Tex("A", "va"), Tex("B", "vb"));
        var @base = Raw(e, Tex("A", "va"), Tex("B", "vb"));
        var other = Raw(e, Tex("B", "vb"));
        var notes = new List<IMergeNote>();

        Described(Merged(TpfMerge.ThreeWay(@base, other, vanilla, notes))).Should().Equal("B=vb");
        notes.Should().BeEmpty();
    }

    [Theory, MemberData(nameof(NameEncodings))]
    public void ThreeWayKeepsATextureBaseRemovedWhenTheOtherLeftItAlone(byte e)
    {
        var vanilla = Raw(e, Tex("A", "va"), Tex("B", "vb"));
        var @base = Raw(e, Tex("B", "vb"));
        var other = Raw(e, Tex("A", "va"), Tex("B", "vb"));
        var notes = new List<IMergeNote>();

        TpfMerge.ThreeWay(@base, other, vanilla, notes).Should().Equal(@base);
        notes.Should().BeEmpty();
    }

    [Theory, MemberData(nameof(NameEncodings))]
    public void ThreeWayKeepsBasesDeletionWithADeletionNoteWhenTheOtherChangedTheTexture(byte e)
    {
        var vanilla = Raw(e, Tex("A", "va"), Tex("B", "vb"));
        var @base = Raw(e, Tex("B", "vb"));
        var other = Raw(e, Tex("A", "oa"), Tex("B", "vb"));
        var notes = new List<IMergeNote>();

        TpfMerge.ThreeWay(@base, other, vanilla, notes, "Mod A", "Mod B").Should().Equal(@base);

        notes.Should().Equal(new TextureDeletionConflict("A", "Mod A", "Mod B", KeptDeleted: true));
        notes.Single().Summary().Should().Be("Mod A deletes the texture A and Mod B changes it; the texture stays deleted.");
    }

    [Theory, MemberData(nameof(NameEncodings))]
    public void ThreeWayKeepsBasesChangedTextureWhenTheOtherDeletedIt(byte e)
    {
        var vanilla = Raw(e, Tex("A", "va"), Tex("B", "vb"));
        var @base = Raw(e, Tex("A", "ba"), Tex("B", "vb"));
        var other = Raw(e, Tex("B", "vb"));
        var notes = new List<IMergeNote>();

        TpfMerge.ThreeWay(@base, other, vanilla, notes, "Mod A", "Mod B").Should().Equal(@base);

        notes.Should().Equal(new TextureDeletionConflict("A", "Mod A", "Mod B", KeptDeleted: false));
        notes.Single().Summary().Should().Be("Mod B deletes the texture A and Mod A changes it; Mod A's copy is kept.");
    }

    [Theory, MemberData(nameof(NameEncodings))]
    public void ThreeWayKeepsAnIdenticalEditBothSidesMadeWithoutANote(byte e)
    {
        // A non-ASCII name, so the two name encodings store different bytes for it.
        var vanilla = Raw(e, Tex("武器_A", "va"), Tex("B", "vb"));
        var @base = Raw(e, Tex("武器_A", "same edit"), Tex("B", "vb"));
        var other = Raw(e, Tex("武器_A", "same edit"), Tex("B", "vb"));
        var notes = new List<IMergeNote>();

        TpfMerge.ThreeWay(@base, other, vanilla, notes).Should().Equal(@base);
        notes.Should().BeEmpty();
    }

    [Theory, MemberData(nameof(NameEncodings))]
    public void ThreeWayKeepsBaseWhenBothAddedTheSameNameDifferently(byte e)
    {
        var vanilla = Raw(e, Tex("V", "vv"));
        var @base = Raw(e, Tex("V", "vv"), Tex("N", "bn"));
        var other = Raw(e, Tex("V", "vv"), Tex("N", "on"));
        var notes = new List<IMergeNote>();

        TpfMerge.ThreeWay(@base, other, vanilla, notes, "Mod A", "Mod B").Should().Equal(@base);
        notes.Should().Equal(new TextureKept("N", "Mod A", "Mod B"));
    }

    [Theory, MemberData(nameof(NameEncodings))]
    public void ThreeWayCountsAMetadataOnlyChangeAsAChange(byte e)
    {
        var vanilla = Raw(e, Tex("A", "same"), Tex("B", "same"), Tex("C", "same"), Tex("D", "same"));
        var @base = Raw(e, Tex("A", "same"), Tex("B", "same"), Tex("C", "same"), Tex("D", "same"));
        var other = Raw(e,
            Tex("A", "same", format: 103), Tex("B", "same", type: 1), Tex("C", "same", mipmaps: 4), Tex("D", "same", flags1: 1));

        var merged = Merged(TpfMerge.ThreeWay(@base, other, vanilla));

        merged.Select(t => (t.Format, t.Type, t.Mipmaps, t.Flags1)).Should().Equal(
            ((byte)103, (byte)0, (byte)1, (byte)0), ((byte)102, (byte)1, (byte)1, (byte)0),
            ((byte)102, (byte)0, (byte)4, (byte)0), ((byte)102, (byte)0, (byte)1, (byte)1));
    }

    [Theory]
    [InlineData("base")]
    [InlineData("other")]
    [InlineData("vanilla")]
    public void ThreeWayRefusesADuplicateTextureNameInAnyInput(string duplicated)
    {
        byte[] Plain() => Raw(1, Tex("A", "aa"));
        byte[] Doubled() => Raw(1, Tex("A", "aa"), Tex("A", "bb"));

        var act = () => TpfMerge.ThreeWay(
            duplicated == "base" ? Doubled() : Plain(),
            duplicated == "other" ? Doubled() : Plain(),
            duplicated == "vanilla" ? Doubled() : Plain());

        act.Should().Throw<TpfException>().WithMessage($"*{duplicated}*'A'*");
    }

    [Fact]
    public void ThreeWayThrowsTpfExceptionForAnInputThatIsNotATpf()
    {
        var good = Raw(1, Tex("A", "aa"));
        var act = () => TpfMerge.ThreeWay(good, "junk"u8.ToArray(), good);
        act.Should().Throw<TpfException>();
    }

    [Fact]
    public void ThreeWayThrowsTpfExceptionWhenTheResultCannotBeWritten()
    {
        // The result keeps base's Shift_JIS encoding, which cannot store the other side's new name.
        var asShiftJis = Tpf.Write(new TpfArchive([Tex("A", "aa")], Platform: 0, Flag2: 3, Encoding: 0));
        var utf16Other = Tpf.Write(new TpfArchive([Tex("A", "aa"), Tex("\u2603", "nn")], Platform: 0, Flag2: 3, Encoding: 1));

        var act = () => TpfMerge.ThreeWay(asShiftJis, utf16Other, asShiftJis);
        act.Should().Throw<TpfException>();
    }
}
