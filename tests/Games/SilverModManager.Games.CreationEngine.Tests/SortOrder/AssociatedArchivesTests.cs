using FluentAssertions;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.Loot;

namespace SilverModManager.Games.CreationEngine.Tests.SortOrder;

// Follows the find_associated_archives rules per game in libloot's archive/find.rs.
public class AssociatedArchivesTests
{
    private static readonly string[] SkyrimArchives =
        ["Mod.bsa", "Mod - Textures.bsa", "Mod - Extra.bsa", "Modern.bsa", "Other.bsa", "Mod.ba2"];

    [Theory]
    [InlineData(LootGame.Skyrim)]
    [InlineData(LootGame.Enderal)]
    public void SkyrimLoadsOnlyTheArchiveNamedLikeThePlugin(LootGame game)
    {
        AssociatedArchives.Find(game, "mod.esp", SkyrimArchives).Should().Equal("Mod.bsa");
    }

    [Theory]
    [InlineData(LootGame.SkyrimSE)]
    [InlineData(LootGame.SkyrimVR)]
    [InlineData(LootGame.EnderalSE)]
    public void SkyrimSpecialEditionAlsoLoadsTheTexturesArchive(LootGame game)
    {
        AssociatedArchives.Find(game, "Mod.esp", SkyrimArchives).Should().Equal("Mod.bsa", "Mod - Textures.bsa");
    }

    [Fact]
    public void ArchiveNamesAreMatchedWithoutCase()
    {
        AssociatedArchives.Find(LootGame.SkyrimSE, "MOD.ESP", ["mod - TEXTURES.BSA", "MOD.bsa"]).Should().Equal("MOD.bsa", "mod - TEXTURES.BSA");
    }

    [Theory]
    [InlineData(LootGame.Fallout3)]
    [InlineData(LootGame.FalloutNV)]
    public void FalloutThreeAndNewVegasLoadEveryBsaThatStartsWithThePluginName(LootGame game)
    {
        AssociatedArchives.Find(game, "Mod.esp", SkyrimArchives).Should().BeEquivalentTo(["Mod.bsa", "Mod - Textures.bsa", "Mod - Extra.bsa", "Modern.bsa"]);
    }

    [Theory]
    [InlineData(LootGame.Fallout4)]
    [InlineData(LootGame.Fallout4VR)]
    public void FalloutFourLoadsEveryBa2ThatStartsWithThePluginNameAndIgnoresBsas(LootGame game)
    {
        AssociatedArchives.Find(game, "Mod.esp", ["Mod - Main.ba2", "Mod - Textures.ba2", "Other.ba2", "Mod.bsa"])
            .Should().Equal("Mod - Main.ba2", "Mod - Textures.ba2");
    }

    [Theory]
    [InlineData(LootGame.Oblivion)]
    [InlineData(LootGame.OblivionRemastered)]
    public void OblivionLoadsPrefixedArchivesForAnEspOnly(LootGame game)
    {
        AssociatedArchives.Find(game, "Mod.esp", ["Mod.bsa", "Mod - Extra.bsa", "Other.bsa"]).Should().Equal("Mod.bsa", "Mod - Extra.bsa");
        AssociatedArchives.Find(game, "Mod.esm", ["Mod.bsa", "Mod - Extra.bsa"]).Should().BeEmpty();
    }

    [Fact]
    public void StarfieldLoadsTheFourSuffixedBa2sInThatOrder()
    {
        string[] archives = ["Mod - Voices_en.ba2", "Mod - Textures.ba2", "Mod - Main.ba2", "Mod - Localization.ba2", "Mod - Voices_fr.ba2", "Mod.ba2"];

        AssociatedArchives.Find(LootGame.Starfield, "Mod.esm", archives)
            .Should().Equal("Mod - Main.ba2", "Mod - Textures.ba2", "Mod - Localization.ba2", "Mod - Voices_en.ba2");
    }

    [Theory]
    [InlineData(LootGame.Morrowind)]
    [InlineData(LootGame.OpenMW)]
    public void MorrowindAndOpenMwLoadNoArchivesForAPlugin(LootGame game)
    {
        AssociatedArchives.Find(game, "Mod.esp", ["Mod.bsa"]).Should().BeEmpty();
    }

    [Theory]
    [InlineData(LootGame.Fallout4, ".ba2")]
    [InlineData(LootGame.Starfield, ".ba2")]
    [InlineData(LootGame.SkyrimSE, ".bsa")]
    [InlineData(LootGame.Oblivion, ".bsa")]
    public void ArchiveExtensionFollowsTheGame(LootGame game, string extension)
    {
        AssociatedArchives.ArchiveExtension(game).Should().Be(extension);
    }
}
