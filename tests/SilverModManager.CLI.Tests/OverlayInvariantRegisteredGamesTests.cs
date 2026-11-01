using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App;
using SilverModManager.Games.FromSoft;
using SilverModManager.Games.FromSoft.ArmoredCore6;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.EldenRingNightreign;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using NSubstitute;
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

    /// <summary>
    /// Every game a merge engine serves, with the online-play fact someone decided for it. A game that starts merging
    /// is not in this table until its fact is stated here, so the rule cannot pass on a default nobody chose.
    /// </summary>
    private static readonly (Type Game, bool Online)[] Merging =
    [
        (typeof(EldenRingGame), true),
        (typeof(ArmoredCore6Game), true),
        (typeof(EldenRingNightreignGame), true),
        (typeof(Witcher3Game), false),
    ];

    public static TheoryData<Type, bool> MergingGames
    {
        get
        {
            var rows = new TheoryData<Type, bool>();
            foreach (var (game, online) in Merging) rows.Add(game, online);
            return rows;
        }
    }

    [Fact]
    public void NoRegisteredGameMergesFilesWithOnlinePlayOutsideAnOverlay()
    {
        var act = () => OverlayInvariant.Check(Games, ServiceProvider.GetRequiredService<MergeEngines>());

        act.Should().NotThrow();
    }

    [Fact]
    public void AMergingGameWithOnlinePlayAndNoOverlayRegisteredNextToTheRealOnesBreaksTheRule()
    {
        var online = Substitute.For<IGameData>();
        online.DisplayName.Returns("Online Merger");
        online.HasOnlinePlay.Returns(true);
        online.DeploysThroughOverlay.Returns(false);
        var engine = Substitute.For<IMergeEngine>();
        engine.Serves(online).Returns(true);
        var engines = new MergeEngines([..ServiceProvider.GetServices<IMergeEngine>(), engine]);

        var act = () => OverlayInvariant.Check([..Games, online], engines);

        act.Should().Throw<InvalidOperationException>().WithMessage("Online Merger *overlay*");
    }

    [Fact]
    public void TheGamesTheMergeEnginesServeAreExactlyTheOnesWithAnOnlinePlayFactBelow()
    {
        var engines = ServiceProvider.GetRequiredService<MergeEngines>();

        var merging = Games.Where(g => engines.For(g) is not null).Select(g => g.GetType());

        merging.Should().BeEquivalentTo(Merging.Select(m => m.Game),
            "a game that starts merging needs its online-play fact stated in Merging, not inherited as false");
    }

    [Theory]
    [MemberData(nameof(MergingGames))]
    public void TheOnlinePlayFactOfEachGameThatMerges(Type gameType, bool online)
        => Games.Single(g => g.GetType() == gameType).HasOnlinePlay.Should().Be(online);

    [Fact]
    public void EveryFromSoftGameDeploysThroughTheOverlay()
        => Games.OfType<IFromSoftGame>().Should().NotBeEmpty().And.OnlyContain(g => ((IGameData)g).DeploysThroughOverlay);

    [Fact]
    public void EveryFromSoftGameHasItsRunToolOnTheOverlayLaunch()
    {
        var tools = ServiceProvider.GetServices<ITool>().Select(t => t.GetType()).ToArray();
        var fromSoft = Games.OfType<IFromSoftGame>().Select(g => g.GetType()).ToArray();

        fromSoft.Should().NotBeEmpty().And.OnlyContain(game => tools.Any(tool => IsRunFromSoftGameOf(tool, game)),
            "DeploysThroughOverlay is only true of a game whose modded launch is RunFromSoftGame's bwrap overlay");
    }

    private static bool IsRunFromSoftGameOf(Type tool, Type game)
    {
        for (var type = tool; type is not null; type = type.BaseType)
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(RunFromSoftGame<>) && type.GetGenericArguments()[0] == game)
                return true;
        return false;
    }
}
