using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.CLI.Types.IpcHandlers;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// Which installations the link handlers may install into: the ones that match the link and have a loadout, with
/// the applied ones flagged.
/// </summary>
public class ManagedGamesTests(ITestOutputHelper helper) : AIsolatedGameTest<ManagedGamesTests, ThunderstoreStubbedGame>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGame<ThunderstoreStubbedGame>()
            .AddUniversalGameLocator<ThunderstoreStubbedGame>(Version.Parse("0.0.0"));
    }

    private ManagedGames Managed(ISynchronizerService? sync = null)
        => new(GameRegistry, Connection, sync ?? Substitute.For<ISynchronizerService>());

    private static bool IsRepo(IGameData game) => ThunderstoreGame.For(game) is { ThunderstoreCommunity: "repo" };

    [Fact]
    public async Task AnInstallationWithALoadoutIsACandidateForItsOwnGame()
    {
        await CreateLoadout();

        var candidates = Managed().Candidates(IsRepo);

        candidates.Should().ContainSingle().Which.Installation.Game.GameId.Should().Be(ThunderstoreStubbedGame.GameId);
        candidates[0].HasAppliedLoadout.Should().BeFalse();
    }

    [Fact]
    public async Task AnInstallationOfAnotherGameIsNotACandidate()
    {
        await CreateLoadout();

        Managed().Candidates(game => ThunderstoreGame.For(game) is { ThunderstoreCommunity: "valheim" }).Should().BeEmpty();
    }

    [Fact]
    public void AnInstallationWithNoLoadoutIsNotACandidate()
    {
        Managed().Candidates(IsRepo).Should().BeEmpty();
    }

    [Fact]
    public async Task AnAppliedLoadoutIsFlagged()
    {
        var loadout = await CreateLoadout();
        var sync = Substitute.For<ISynchronizerService>();
        sync.TryGetLastAppliedLoadout(Arg.Any<GameInstallation>(), out Arg.Any<Loadout.ReadOnly>())
            .Returns(call => { call[1] = loadout; return true; });

        Managed(sync).Candidates(IsRepo).Should().ContainSingle().Which.HasAppliedLoadout.Should().BeTrue();
    }

    [Fact]
    public async Task TheLoadoutOfAnInstallationIsTheLastAppliedOne()
    {
        var first = await CreateLoadout();
        var applied = await CreateLoadout();
        var sync = Substitute.For<ISynchronizerService>();
        sync.TryGetLastAppliedLoadout(Arg.Any<GameInstallation>(), out Arg.Any<Loadout.ReadOnly>())
            .Returns(call => { call[1] = applied; return true; });

        var chosen = Managed(sync).LoadoutOf(GameInstallation);

        chosen.Id.Should().Be(applied.LoadoutId);
        chosen.Id.Should().NotBe(first.LoadoutId);
        chosen.Name.Should().Be(applied.Name);
    }

    [Fact]
    public async Task WithNoAppliedLoadoutTheLoadoutOfAnInstallationIsItsFirst()
    {
        var first = await CreateLoadout();
        await CreateLoadout();

        var chosen = Managed().LoadoutOf(GameInstallation);

        chosen.Id.Should().Be(first.LoadoutId);
        chosen.Name.Should().Be(first.Name);
    }

    private GameInstallation SecondInstallationOfTheSameGame() => new(
        new GameLocatorResult
        {
            Game = GameInstallation.Game,
            Path = default,
            Store = GameStore.Unknown,
            StoreIdentifier = "second",
            LocatorIds = [],
            Locator = Substitute.For<IGameLocator>(),
        },
        default);

    // Two installations of one game, with every loadout on GameInstallation, the way the registry tells them apart.
    private ManagedGames ManagedWithASecondInstallation(GameInstallation second)
    {
        var registry = Substitute.For<IGameRegistry>();
        registry.LocateGameInstallations().Returns([second, GameInstallation]);
        registry.TryGetGameInstallation(Arg.Any<Loadout.ReadOnly>(), out Arg.Any<GameInstallation?>())
            .Returns(call => { call[1] = GameInstallation; return true; });
        return new ManagedGames(registry, Connection, Substitute.For<ISynchronizerService>());
    }

    [Fact]
    public async Task OnlyTheInstallationThatOwnsALoadoutIsAnInstallationCandidate()
    {
        await CreateLoadout();

        var candidates = ManagedWithASecondInstallation(SecondInstallationOfTheSameGame()).InstallationCandidates(IsRepo);

        candidates.Should().ContainSingle().Which.Installation.Should().BeSameAs(GameInstallation);
    }

    [Fact]
    public async Task TheGameCandidatesStillCountEveryInstallationOfTheGame()
    {
        await CreateLoadout();

        ManagedWithASecondInstallation(SecondInstallationOfTheSameGame()).Candidates(IsRepo).Should().HaveCount(2);
    }

    [Fact]
    public async Task AnInstallationWithoutLoadoutsDoesNotBorrowTheLoadoutOfAnotherInstallation()
    {
        await CreateLoadout();
        var second = SecondInstallationOfTheSameGame();

        var act = () => ManagedWithASecondInstallation(second).LoadoutOf(second);

        act.Should().Throw<InvalidOperationException>();
    }
}
