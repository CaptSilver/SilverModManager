using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Witcher.Witcher1;
using SilverModManager.Games.Witcher.Witcher2;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Witcher.Tests;

public class WitcherRunGameToolTests(IServiceProvider serviceProvider)
{
    // The Play button launches through the game's run tool; without one it has nothing to start.
    [Fact]
    public void Witcher1_HasOneRunGameTool() => RunGameToolsFor(Witcher1Game.GameId).Should().ContainSingle();

    [Fact]
    public void Witcher2_HasOneRunGameTool() => RunGameToolsFor(Witcher2Game.GameId).Should().ContainSingle();

    [Fact]
    public void Witcher3_HasOneRunGameTool() => RunGameToolsFor(Witcher3Game.GameId).Should().ContainSingle();

    private IRunGameTool[] RunGameToolsFor(GameId gameId) => serviceProvider.GetServices<ITool>()
        .OfType<IRunGameTool>()
        .Where(tool => tool.GameIds.Contains(gameId))
        .ToArray();
}
