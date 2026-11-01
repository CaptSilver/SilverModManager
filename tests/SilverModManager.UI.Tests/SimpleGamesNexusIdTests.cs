using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.App.UI.Helpers;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.UI.Tests;

/// <summary>
/// Games built from definitions are one class registered many times, so the places that read a game's Nexus id
/// have to find each of them from the registered games, as they do for any other game.
/// </summary>
public class SimpleGamesNexusIdTests(IServiceProvider provider)
{
    private IGameData[] RegisteredRows => provider.GetServices<IGameData>()
        .Where(game => SimpleGames.All.Any(row => row.GameId == game.GameId))
        .ToArray();

    [Fact]
    public void EveryRow_IsRegisteredWithTheApp()
    {
        RegisteredRows.Select(game => game.GameId).Should().BeEquivalentTo(SimpleGames.All.Select(row => row.GameId));
    }

    [Fact]
    public void NxmRouting_PicksEachRowByItsOwnNexusId()
    {
        foreach (var row in SimpleGames.All.Where(row => row.NexusModsGameId.HasValue))
        {
            NexusModsGameScope.ResolveNxmGame(RegisteredRows, row.NexusModsGameId.Value, _ => true, NullLogger.Instance)
                .Value.GameId.Should().Be(row.GameId);
        }
    }

    [Fact]
    public void NxmRouting_ForAnotherGamesId_PicksNoRow()
    {
        NexusModsGameScope.ResolveNxmGame(RegisteredRows, NexusModsGameId.From(4242), _ => true, NullLogger.Instance)
            .HasValue.Should().BeFalse();
    }
}
