using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.App.UI.Helpers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.UI.Tests;

/// <summary>
/// Every FromSoft game takes mods from Nexus Mods, so the places that read a game's Nexus id route Nexus links to the
/// right one of them and to nothing for an id that is not theirs.
/// </summary>
public class FromSoftGamesNexusIdTests(IServiceProvider provider)
{
    private IGameData[] FromSoftGames => provider.GetServices<IGameData>()
        .Where(game => game.GameId.ToString().StartsWith("FromSoft.", StringComparison.Ordinal))
        .ToArray();

    [Fact]
    public void EveryFromSoftGameIsOnNexusMods()
        => FromSoftGames.Should().OnlyContain(game => game.IsOnNexusMods());

    [Theory]
    [InlineData("FromSoft.EldenRing", 4333u)]
    [InlineData("FromSoft.ArmoredCore6", 5679u)]
    [InlineData("FromSoft.EldenRingNightreign", 7800u)]
    public void NxmRoutingPicksEachGameByItsOwnNexusId(string gameId, uint nexusId)
    {
        NexusModsGameScope.ResolveNxmGame(FromSoftGames, NexusModsGameId.From(nexusId), _ => true, NullLogger.Instance)
            .Value.GameId.Should().Be(GameId.From(gameId));
    }

    [Fact]
    public void NxmRoutingForAnotherGamesIdPicksNone()
    {
        NexusModsGameScope.ResolveNxmGame(FromSoftGames, NexusModsGameId.From(4242), _ => true, NullLogger.Instance)
            .HasValue.Should().BeFalse();
    }
}
