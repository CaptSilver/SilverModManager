using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.Generic.Tests;

public class GameSourcesTests
{
    private static readonly GitHubShelfEntryDefinition Entry = new("garyttierney", "me3", ".7z", "me3", "Mod loader");

    private static FixtureGame GameWith(GameSources sources)
        => new(new ServiceCollection().BuildServiceProvider(), new FixtureDefinition { GameId = GameId.From("Tests.Sources"), DisplayName = "Sources", Sources = sources });

    [Fact]
    public void ADefinitionsSourcesReachTheGameClass()
    {
        var game = GameWith(new GameSources(GitHubShelf: new GitHubShelfSupport([Entry])));

        GitHubShelfGame.For(game).Should().NotBeNull().And.Subject.As<IGitHubShelfGame>().GitHubShelf.Should().Equal(Entry);
    }

    [Fact]
    public void ADefinitionWithoutSourcesIsServedByNone()
    {
        GitHubShelfGame.For(GameWith(GameSources.None)).Should().BeNull();
    }

    [Fact]
    public void AHandWrittenGameImplementsTheMarkerItself()
    {
        var game = Substitute.For<IGameData, IGitHubShelfGame>();

        GitHubShelfGame.For(game).Should().BeSameAs(game);
    }

    [Fact]
    public void ATypeThatIsNeitherIsServedByNone()
    {
        GitHubShelfGame.For(Substitute.For<IGameData>()).Should().BeNull();
    }

    [Fact]
    public void ADefinitionsGameBananaSideReachesTheGameClass()
    {
        var support = new GameBananaSupport(297, [GameBananaSection.Mod, GameBananaSection.Skin]);
        var game = GameWith(new GameSources(GameBanana: support));

        GameBananaGame.For(game).Should().BeSameAs(support);
        GitHubShelfGame.For(game).Should().BeNull("a game GameBanana serves is not on the GitHub shelf because of it");
    }

    [Fact]
    public void ADefinitionWithoutGameBananaIsNotServedByIt()
    {
        GameBananaGame.For(GameWith(GameSources.None)).Should().BeNull();
        GameBananaGame.For(Substitute.For<IGameData>()).Should().BeNull();
    }

    [Fact]
    public void AHandWrittenGameImplementsTheGameBananaMarkerItself()
    {
        var game = Substitute.For<IGameData, IGameBananaGame>();

        GameBananaGame.For(game).Should().BeSameAs(game);
    }

    [Fact]
    public void TeamFortress2IsServedByGameBanana()
    {
        // https://gamebanana.com/apiv11/Game/297/ProfilePage answers "Team Fortress 2".
        var support = GameFolderSimpleGames.TeamFortress2.Sources.GameBanana;

        support.Should().NotBeNull();
        support!.GameBananaGameId.Should().Be(297);
        support.GameBananaSections.Should().Equal(GameBananaSection.Mod);
    }
}
