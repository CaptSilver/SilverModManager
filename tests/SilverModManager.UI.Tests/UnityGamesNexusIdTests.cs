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
/// Every Unity game takes mods from Thunderstore and from Nexus Mods, so the places that read a game's Nexus id have to
/// route Nexus links to them like any other game.
/// </summary>
public class UnityGamesNexusIdTests(IServiceProvider provider)
{
    private static readonly NexusModsGameId UnrelatedNexusId = NexusModsGameId.From(4242);

    private IGameData[] UnityGames => provider.GetServices<IGameData>()
        .Where(game => game.GameId.ToString().StartsWith("Unity.", StringComparison.Ordinal))
        .ToArray();

    [Fact]
    public void EveryUnityGameIsRegisteredWithTheApp()
    {
        UnityGames.Select(game => game.GameId).Should().BeEquivalentTo(
            new[] { "Unity.Repo", "Unity.Peak", "Unity.Valheim", "Unity.Subnautica", "Unity.SubnauticaBelowZero", "Unity.HollowKnightSilksong" }.Select(GameId.From));
    }

    [Fact]
    public void EveryUnityGameIsOnNexusMods()
    {
        UnityGames.Should().OnlyContain(game => game.IsOnNexusMods());
    }

    [Fact]
    public void NoUnityGameOwnsNexusCollectionsInAnEmptyDatabase()
    {
        var db = provider.GetRequiredService<IConnection>().Db;

        foreach (var game in UnityGames) game.CollectionsOf(db).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Unity.Repo", 7398u)]
    [InlineData("Unity.Peak", 7867u)]
    [InlineData("Unity.Valheim", 3667u)]
    [InlineData("Unity.Subnautica", 1155u)]
    [InlineData("Unity.SubnauticaBelowZero", 2706u)]
    [InlineData("Unity.HollowKnightSilksong", 8136u)]
    public void NxmRoutingPicksEachGameByItsOwnNexusId(string gameId, uint nexusId)
    {
        NexusModsGameScope.ResolveNxmGame(UnityGames, NexusModsGameId.From(nexusId), _ => true, NullLogger.Instance)
            .Value.GameId.Should().Be(GameId.From(gameId));
    }

    [Fact]
    public void NxmRoutingForAnotherGamesIdPicksNone()
    {
        NexusModsGameScope.ResolveNxmGame(UnityGames, UnrelatedNexusId, _ => true, NullLogger.Instance)
            .HasValue.Should().BeFalse();
    }
}
