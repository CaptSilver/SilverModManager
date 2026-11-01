using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Games.Generic.Tests.Simple;

/// <summary>
/// Two games built from definitions are two instances of one CLR type, so nothing about them can be resolved by type.
/// Each registration has to give exactly one game, visible as <see cref="IGame"/> and <see cref="IGameData"/>, and one
/// launch tool.
/// </summary>
public class AddDefinedGameTests(ITestOutputHelper helper) : AIsolatedDefinedGameTest<AddDefinedGameTests>(helper, First.GameId)
{
    private static readonly SimpleGameDefinition First = SimpleDefinitions.With(new ModLocation(ModLocationRoot.GameFolder, "Mods"), "Tests.First");
    private static readonly SimpleGameDefinition Second = SimpleDefinitions.With(new ModLocation(ModLocationRoot.GameFolder, "Mods"), "Tests.Second");

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddDefinedGame(First, (provider, definition) => new SimpleGame(provider, definition))
            .AddDefinedGame(Second, (provider, definition) => new SimpleGame(provider, definition))
            .AddUniversalGameLocator(First.GameId, locationsFileSystem: IsolatedKnownFolders.Create)
            .AddUniversalGameLocator(Second.GameId, locationsFileSystem: IsolatedKnownFolders.Create);
    }

    [Fact]
    public void EachDefinition_IsOneGame()
    {
        var games = ServiceProvider.GetServices<IGame>().Where(game => game.GameId == First.GameId || game.GameId == Second.GameId).ToArray();

        games.Should().HaveCount(2);
        games.Select(game => game.GameId).Should().BeEquivalentTo([First.GameId, Second.GameId]);
    }

    [Fact]
    public void TheGameAndItsGameData_AreTheSameInstance()
    {
        foreach (var id in new[] { First.GameId, Second.GameId })
        {
            var game = ServiceProvider.GetServices<IGame>().Single(g => g.GameId == id);
            var data = ServiceProvider.GetServices<IGameData>().Single(g => g.GameId == id);
            data.Should().BeSameAs(game);
        }
    }

    [Fact]
    public void EachDefinition_HasOneLaunchTool()
    {
        var tools = ServiceProvider.GetServices<ITool>().OfType<IRunGameTool>().ToArray();

        foreach (var id in new[] { First.GameId, Second.GameId })
            tools.Where(tool => tool.GameIds.Contains(id)).Should().ContainSingle();
    }

    [Fact]
    public void TheBootedGame_IsTheRegisteredInstance()
    {
        Game.Should().BeSameAs(ServiceProvider.GetServices<IGame>().Single(game => game.GameId == First.GameId));
    }

    [Fact]
    public void TheBootedGame_UsesTheDefaultSynchronizer()
    {
        Game.Synchronizer.Should().BeOfType<SimpleGameSynchronizer>();
    }
}
