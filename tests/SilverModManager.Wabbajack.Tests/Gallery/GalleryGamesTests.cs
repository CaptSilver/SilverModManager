using FluentAssertions;
using SilverModManager.Sdk.Games;
using SilverModManager.Wabbajack.Gallery;

namespace SilverModManager.Wabbajack.Tests.Gallery;

public class GalleryGamesTests
{
    [Theory]
    [InlineData("skyrimspecialedition", "CreationEngine.SkyrimSE")]
    [InlineData("skyrim", "CreationEngine.SkyrimLE")]
    [InlineData("fallout4", "CreationEngine.Fallout4")]
    [InlineData("falloutnewvegas", "CreationEngine.FalloutNewVegas")]
    [InlineData("fallout3", "CreationEngine.Fallout3")]
    [InlineData("oblivion", "CreationEngine.Oblivion")]
    [InlineData("morrowind", "CreationEngine.Morrowind")]
    [InlineData("enderal", "CreationEngine.Enderal")]
    [InlineData("enderalspecialedition", "CreationEngine.EnderalSE")]
    [InlineData("starfield", "CreationEngine.Starfield")]
    [InlineData("cyberpunk2077", "RedEngine.Cyberpunk2077")]
    [InlineData("baldursgate3", "Larian.BaldursGate3")]
    [InlineData("SkyrimSpecialEdition", "CreationEngine.SkyrimSE")]
    public void GalleryGamesMapToTheAppsGames(string galleryGame, string gameId)
    {
        GalleryGames.GameIdFor(galleryGame).Should().Be(GameId.From(gameId));
    }

    [Theory]
    [InlineData("stardewvalley")]
    [InlineData("")]
    [InlineData("nosuchgame")]
    public void AGameTheAppHasNoTitleForHasNoMapping(string galleryGame)
    {
        GalleryGames.GameIdFor(galleryGame).Should().BeNull();
    }
}
