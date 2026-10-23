using NexusMods.Games.Bethesda.Loot.Masterlists;

namespace NexusMods.Games.Bethesda.Loot.Tests.Masterlists;

public class LootGameTests
{
    [Theory]
    [InlineData(LootGame.Morrowind, "morrowind")]
    [InlineData(LootGame.OpenMW, "morrowind")]
    [InlineData(LootGame.Oblivion, "oblivion")]
    [InlineData(LootGame.OblivionRemastered, "oblivion-remastered")]
    [InlineData(LootGame.Skyrim, "skyrim")]
    [InlineData(LootGame.SkyrimSE, "skyrimse")]
    [InlineData(LootGame.SkyrimVR, "skyrimse")]
    [InlineData(LootGame.Fallout3, "fallout3")]
    [InlineData(LootGame.FalloutNV, "falloutnv")]
    [InlineData(LootGame.Fallout4, "fallout4")]
    [InlineData(LootGame.Fallout4VR, "fallout4")]
    [InlineData(LootGame.Starfield, "starfield")]
    [InlineData(LootGame.Enderal, "enderal")]
    [InlineData(LootGame.EnderalSE, "enderal")]
    public void GameMapsToItsMasterlistRepository(LootGame game, string repository)
    {
        game.MasterlistRepository().Should().Be(repository);
    }

    [Fact]
    public void EveryGameMapsToABundledRepository()
    {
        foreach (var game in Enum.GetValues<LootGame>())
            LootGameExtensions.BundledRepositories.Should().Contain(game.MasterlistRepository(), game.ToString());
    }
}
