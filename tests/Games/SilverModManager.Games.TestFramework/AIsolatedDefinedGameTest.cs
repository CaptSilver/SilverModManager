using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.TestFramework;

/// <summary>
/// The isolated game fixture for a game built from a definition. It boots the installation whose game has the given
/// <see cref="GameId"/>; pair it with <c>AddUniversalGameLocator(GameId)</c>. The setup is the one
/// <see cref="AIsolatedGameTest{TTest,TGame}"/> already has.
/// </summary>
public abstract class AIsolatedDefinedGameTest<TTest>(ITestOutputHelper helper, GameId gameId)
    : AIsolatedGameTest<TTest, IGame>(helper, gameId);
