using FluentAssertions;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Merge;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Games.FromSoft.Tests.Formats;

namespace NexusMods.Games.FromSoft.Tests.Merge;

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
}
