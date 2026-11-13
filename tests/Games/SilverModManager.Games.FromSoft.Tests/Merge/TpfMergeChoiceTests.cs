using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Merge;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

public class TpfMergeChoiceTests
{
    private const string Base = "Mod A";
    private const string Next = "Mod B";

    private static FromSoftFormatTraits T => EldenRingGame.Definition.Formats;

    private static TpfArchive Archive(params (string Name, string Data)[] textures)
        => new([.. textures.Select(t => new Texture(t.Name, System.Text.Encoding.ASCII.GetBytes(t.Data), 102, 0, 1, 0))], Platform: 0, Flag2: 3, Encoding: 1);

    private static byte[] Packed(params (string Name, string Data)[] textures) => Dcx.Write(Tpf.Write(Archive(textures)), T.PackedWrite);

    private static byte[] Raw(params (string Name, string Data)[] textures) => Tpf.Write(Archive(textures));

    private static Dictionary<string, string> Names(TpfArchive archive)
        => archive.Textures.ToDictionary(t => t.Name, t => System.Text.Encoding.ASCII.GetString(t.Data));

    [Fact]
    public void ChosenNext_TakesTheNextTexture()
    {
        var choices = new PathChoices([("tpf:Icon", Next)]);

        var merged = TpfMerge.Union(Packed(("Icon", "base")), Packed(("Icon", "next")), T, null, Base, Next, choices);

        Names(Tpf.Read(Dcx.Read(merged, T))).Should().Equal(new Dictionary<string, string> { ["Icon"] = "next" });
        choices.Owner("tpf:Icon").Should().Be(Next);
    }

    [Fact]
    public void ChosenBase_KeepsTheBaseTexture()
    {
        var choices = new PathChoices([("tpf:Icon", Base)]);
        var @base = Packed(("Icon", "base"));

        TpfMerge.Union(@base, Packed(("Icon", "next")), T, null, Base, Next, choices).Should().Equal(@base);
        choices.Owner("tpf:Icon").Should().BeNull();
    }

    [Fact]
    public void ChosenNext_TakesTheNextTextureOfAThreeWayConflict()
    {
        var choices = new PathChoices([("tpf:Icon", Next)]);

        var merged = TpfMerge.ThreeWay(Raw(("Icon", "base")), Raw(("Icon", "next")), Raw(("Icon", "vanilla")), null, Base, Next, choices: choices);

        Names(Tpf.Read(merged)).Should().Equal(new Dictionary<string, string> { ["Icon"] = "next" });
    }

    [Fact]
    public void TextureDeletion_ChosenDeleterKeepsItDeleted()
    {
        var choices = new PathChoices([("tpf:Icon", Next)]);
        var notes = new List<SilverModManager.Abstractions.Games.Merge.IMergeNote>();

        // Base changed the texture and Next deleted it; without a pin base's copy stays.
        var kept = TpfMerge.ThreeWay(Raw(("Icon", "base"), ("Other", "x")), Raw(("Other", "x")), Raw(("Icon", "vanilla"), ("Other", "x")), null, Base, Next);
        Names(Tpf.Read(kept)).Should().ContainKey("Icon");

        var merged = TpfMerge.ThreeWay(Raw(("Icon", "base"), ("Other", "x")), Raw(("Other", "x")), Raw(("Icon", "vanilla"), ("Other", "x")), notes, Base, Next, choices: choices);

        Names(Tpf.Read(merged)).Should().Equal(new Dictionary<string, string> { ["Other"] = "x" });
        choices.Owner("tpf:Icon").Should().Be(Next);
        notes.Should().ContainSingle().Which.Should().BeOfType<TextureDeletionConflict>();
    }

    [Fact]
    public void TextureDeletion_ChosenChangerOverBaseDeletionRestoresIt()
    {
        var choices = new PathChoices([("tpf:Icon", Next)]);

        // Base deleted the texture and Next changed it; the pin brings Next's copy back.
        var merged = TpfMerge.ThreeWay(Raw(("Other", "x")), Raw(("Icon", "next"), ("Other", "x")), Raw(("Icon", "vanilla"), ("Other", "x")), null, Base, Next, choices: choices);

        Names(Tpf.Read(merged)).Should().Equal(new Dictionary<string, string> { ["Other"] = "x", ["Icon"] = "next" });
    }
}
