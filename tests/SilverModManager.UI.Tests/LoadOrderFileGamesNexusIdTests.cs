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
/// Every load-order-file game takes mods from Nexus Mods, so the places that read a game's Nexus id have to route Nexus links to
/// each of them among all the registered games.
/// </summary>
public class LoadOrderFileGamesNexusIdTests(IServiceProvider provider)
{
    private static readonly NexusModsGameId UnrelatedNexusId = NexusModsGameId.From(4242);

    private IGameData[] FamilyGames => provider.GetServices<IGameData>()
        .Where(game => game.GameId.ToString().StartsWith("LoadOrderFile.", StringComparison.Ordinal))
        .ToArray();

    [Fact]
    public void EveryLoadOrderFileGameIsRegisteredWithTheApp()
    {
        FamilyGames.Select(game => game.GameId).Should().BeEquivalentTo(
            new[] { "LoadOrderFile.KingdomComeDeliverance", "LoadOrderFile.KingdomComeDeliverance2" }.Select(GameId.From));
    }

    [Fact]
    public void EveryLoadOrderFileGameIsOnNexusMods()
    {
        FamilyGames.Should().OnlyContain(game => game.IsOnNexusMods());
    }

    [Fact]
    public void NoLoadOrderFileGameOwnsNexusCollectionsInAnEmptyDatabase()
    {
        var db = provider.GetRequiredService<IConnection>().Db;

        foreach (var game in FamilyGames) game.CollectionsOf(db).Should().BeEmpty();
    }

    [Theory]
    [InlineData("LoadOrderFile.KingdomComeDeliverance", 2298u)]
    [InlineData("LoadOrderFile.KingdomComeDeliverance2", 7286u)]
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
