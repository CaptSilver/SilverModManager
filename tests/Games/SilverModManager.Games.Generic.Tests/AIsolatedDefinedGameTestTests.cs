using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Games.Generic.Tests;

/// <summary>
/// The isolated fixture boots a game picked by <see cref="GameId"/>, so a game that exists only as a definition can be
/// tested like a hand-written one. Two definition-built games share a CLR type, which is what a type-keyed lookup
/// could not tell apart.
/// </summary>
public class AIsolatedDefinedGameTestTests(ITestOutputHelper helper)
    : AIsolatedDefinedGameTest<AIsolatedDefinedGameTestTests>(helper, Target)
{
    private static readonly GameId Target = GameId.From("Tests.Target");
    private static readonly GameId Bystander = GameId.From("Tests.Bystander");

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddSingleton<IGame>(sp => FixtureGame.Create(sp, Bystander.ToString()))
            .AddSingleton<IGame>(sp => FixtureGame.Create(sp, Target.ToString()))
            .AddUniversalGameLocator(Bystander, locationsFileSystem: IsolatedKnownFolders.Create)
            .AddUniversalGameLocator(Target, locationsFileSystem: IsolatedKnownFolders.Create);
    }

    [Fact]
    public void Boot_SelectsTheInstallationOfTheGivenGameId()
    {
        GameInstallation.Game.GameId.Should().Be(Target);
        Game.GameId.Should().Be(Target);
    }

    [Fact]
    public void Boot_LocatesTheGameFolderOfTheSelectedGameOnly()
    {
        GameInstallation.LocatorResult.Path.FileName.Should().Contain(Target.ToString());
        GameInstallation.LocatorResult.Path.FileName.Should().NotContain(Bystander.ToString());
    }

    [Fact]
    public async Task CreateLoadout_WorksForADefinitionBuiltGame()
    {
        var loadout = await CreateLoadout();

        loadout.InstallationInstance.Game.GameId.Should().Be(Target);
    }
}
