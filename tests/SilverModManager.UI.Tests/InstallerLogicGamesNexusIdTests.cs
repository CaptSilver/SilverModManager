using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.App.UI.Helpers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.UI.Tests;

/// <summary>
/// The games whose archives need installer rules are registered like any other simple game, so a Nexus link has to reach
/// each by its own id. Neverwinter Nights shares its Nexus domain name with another game; its id is the one pinned here.
/// </summary>
public class InstallerLogicGamesNexusIdTests(IServiceProvider provider)
{
    private IGameData[] Registered => provider.GetServices<IGameData>().ToArray();

    [Theory]
    [InlineData("Simple.KnightsOfTheOldRepublic", 234u)]
    [InlineData("Simple.NeverwinterNightsEnhancedEdition", 180u)]
    [InlineData("Simple.TheSims4", 641u)]
    public void NxmRoutingPicksEachGameByItsOwnNexusId(string gameId, uint nexusId)
    {
        NexusModsGameScope.ResolveNxmGame(Registered, NexusModsGameId.From(nexusId), _ => true, NullLogger.Instance)
            .Value.GameId.Should().Be(GameId.From(gameId));
    }

    [Fact]
    public void TheOnlineNeverwinterGamesIdPicksNoGame()
    {
        NexusModsGameScope.ResolveNxmGame(Registered, NexusModsGameId.From(8703), _ => true, NullLogger.Instance)
            .HasValue.Should().BeFalse();
    }
}
