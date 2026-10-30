using FluentAssertions;
using SilverModManager.Abstractions.Games;
using NSubstitute;

namespace SilverModManager.Collections.Tests;

public class RequireGameTests
{
    [Fact]
    public void AGameThatIsAnIGameIsReturned()
    {
        var game = Substitute.For<IGame>();

        InstallCollectionJob.RequireGame(game).Should().BeSameAs(game);
    }

    [Fact]
    public void ALocatableThatIsNotAnIGameThrowsNamingItsType()
    {
        var act = () => InstallCollectionJob.RequireGame(new NotAGame());

        act.Should().Throw<InvalidOperationException>().WithMessage("*NotAGame*IGame*");
    }

    private sealed class NotAGame;
}
