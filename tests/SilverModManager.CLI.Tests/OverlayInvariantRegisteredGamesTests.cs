using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.App;
using SilverModManager.Games.FromSoft;
using SilverModManager.Games.FromSoft.ArmoredCore6;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.EldenRingNightreign;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// The overlay rule over every game the app registers: a game that merges files and has online play must deploy
/// through an overlay, so a Steam launch never goes online on a game folder the app wrote mods into.
/// </summary>
public class OverlayInvariantRegisteredGamesTests(ITestOutputHelper helper)
    : AIsolatedGameTest<OverlayInvariantRegisteredGamesTests, EldenRingGame>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services)
            .AddSupportedGames()
            .AddTextMergeEngine()
            // The fixture needs one installed game to start; the rule reads the registered games, not installations.
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<EldenRingGame>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam])
            {
                LocatorIds = [LocatorId.From("3976631895")],
            });

    private IGameData[] Games => ServiceProvider.GetServices<IGame>().Cast<IGameData>().ToArray();

    [Fact]
    public void NoRegisteredGameMergesFilesWithOnlinePlayOutsideAnOverlay()
    {
        var act = () => OverlayInvariant.Check(Games, ServiceProvider.GetRequiredService<MergeEngines>());

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(typeof(EldenRingGame), true)]
    [InlineData(typeof(ArmoredCore6Game), true)]
    [InlineData(typeof(EldenRingNightreignGame), true)]
    [InlineData(typeof(Witcher3Game), false)]
    public void TheOnlinePlayFactOfTheGamesThatMerge(Type gameType, bool online)
        => Games.Single(g => g.GetType() == gameType).HasOnlinePlay.Should().Be(online);

    [Fact]
    public void EveryFromSoftGameDeploysThroughTheOverlay()
        => Games.OfType<IFromSoftGame>().Should().NotBeEmpty().And.OnlyContain(g => ((IGameData)g).DeploysThroughOverlay);

    [Fact]
    public void EveryGameTheMergeEnginesServeIsEnumerated()
    {
        var engines = ServiceProvider.GetRequiredService<MergeEngines>();

        var merging = Games.Where(g => engines.For(g) is not null).Select(g => g.DisplayName).ToArray();

        merging.Should().Contain(n => n.Contains("Witcher")).And.Contain(n => n.Contains("Elden Ring"));
    }
}
