using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>Which folder of a Creation Engine game can be mounted. It does not depend on where the game is installed.</summary>
public class MountRootsTests(IServiceProvider serviceProvider)
{
    private IGame GameOf<TGame>() where TGame : class, IGame => serviceProvider.GetServices<IGame>().OfType<TGame>().Single();

    // MountRoots only names a folder of the game, so no installation is read.
    private static IReadOnlyList<GamePath> RootsOf(IGame game) => ((IMountableGame)game).MountRoots(null!);

    [Fact]
    public void SkyrimSE_DeclaresGameData() =>
        RootsOf(GameOf<CreationEngine.SkyrimSE.SkyrimSE>()).Should().Equal(new GamePath(LocationId.Game, "Data"));

    [Fact]
    public void Morrowind_DeclaresGameDataFiles() =>
        RootsOf(GameOf<CreationEngine.Morrowind.Morrowind>()).Should().Equal(new GamePath(LocationId.Game, "Data Files"));

    [Fact]
    public void Fallout76_IsNotMountable() =>
        GameOf<CreationEngine.Fallout76.Fallout76>().Should().NotBeAssignableTo<IMountableGame>();
}
