using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.App.UI.Helpers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.UI.Tests;

/// <summary>
/// Every RE Engine game takes mods from Nexus Mods, so the places that read a game's Nexus id have to route Nexus links to
/// each of them among all the registered games.
/// </summary>
public class ReEngineGamesNexusIdTests(IServiceProvider provider)
{
    private static readonly NexusModsGameId UnrelatedNexusId = NexusModsGameId.From(4242);

    private IGameData[] FamilyGames => provider.GetServices<IGameData>()
        .Where(game => game.GameId.ToString().StartsWith("ReEngine.", StringComparison.Ordinal))
        .ToArray();

    [Fact]
    public void EveryREEngineGameIsRegisteredWithTheApp()
    {
        FamilyGames.Select(game => game.GameId).Should().BeEquivalentTo(
            new[] { "ReEngine.DevilMayCry5", "ReEngine.ResidentEvil2", "ReEngine.ResidentEvil3", "ReEngine.MonsterHunterRise", "ReEngine.MonsterHunterWilds" }.Select(GameId.From));
    }

    [Fact]
    public void EveryREEngineGameIsOnNexusMods()
    {
        FamilyGames.Should().OnlyContain(game => game.IsOnNexusMods());
    }

    [Fact]
    public void NoREEngineGameOwnsNexusCollectionsInAnEmptyDatabase()
    {
        var db = provider.GetRequiredService<IConnection>().Db;

        foreach (var game in FamilyGames) game.CollectionsOf(db).Should().BeEmpty();
    }

    [Theory]
    [InlineData("ReEngine.DevilMayCry5", 2751u)]
    [InlineData("ReEngine.ResidentEvil2", 2702u)]
    [InlineData("ReEngine.ResidentEvil3", 3191u)]
    [InlineData("ReEngine.MonsterHunterRise", 4095u)]
    [InlineData("ReEngine.MonsterHunterWilds", 6993u)]
    public void NxmRoutingPicksEachGameByItsOwnNexusId(string gameId, uint nexusId)
    {
        NexusModsGameScope.ResolveNxmGame(FamilyGames, NexusModsGameId.From(nexusId), _ => true, NullLogger.Instance)
            .Value.GameId.Should().Be(GameId.From(gameId));
    }

    [Fact]
    public void NxmRoutingForAnotherGamesIdPicksNone()
    {
        NexusModsGameScope.ResolveNxmGame(FamilyGames, UnrelatedNexusId, _ => true, NullLogger.Instance)
            .HasValue.Should().BeFalse();
    }
}
