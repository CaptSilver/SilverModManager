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
/// Every Unreal Engine game takes mods from Nexus Mods, so the places that read a game's Nexus id have to route Nexus links to
/// each of them among all the registered games.
/// </summary>
public class UnrealGamesNexusIdTests(IServiceProvider provider)
{
    private static readonly NexusModsGameId UnrelatedNexusId = NexusModsGameId.From(4242);

    private IGameData[] FamilyGames => provider.GetServices<IGameData>()
        .Where(game => game.GameId.ToString().StartsWith("Unreal.", StringComparison.Ordinal))
        .ToArray();

    [Fact]
    public void EveryUnrealEngineGameIsRegisteredWithTheApp()
    {
        FamilyGames.Select(game => game.GameId).Should().BeEquivalentTo(
            new[] { "Unreal.FinalFantasy7Remake", "Unreal.FinalFantasy7Rebirth", "Unreal.Stalker2", "Unreal.Palworld", "Unreal.ReadyOrNot", "Unreal.Dispatch" }.Select(GameId.From));
    }

    [Fact]
    public void EveryUnrealEngineGameIsOnNexusMods()
    {
        FamilyGames.Should().OnlyContain(game => game.IsOnNexusMods());
    }

    [Fact]
    public void NoUnrealEngineGameOwnsNexusCollectionsInAnEmptyDatabase()
    {
        var db = provider.GetRequiredService<IConnection>().Db;

        foreach (var game in FamilyGames) game.CollectionsOf(db).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Unreal.FinalFantasy7Remake", 4202u)]
    [InlineData("Unreal.FinalFantasy7Rebirth", 7237u)]
    [InlineData("Unreal.Stalker2", 6944u)]
    [InlineData("Unreal.Palworld", 6063u)]
    [InlineData("Unreal.ReadyOrNot", 4205u)]
    [InlineData("Unreal.Dispatch", 8325u)]
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
