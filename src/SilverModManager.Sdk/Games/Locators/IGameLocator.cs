using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// Represents a game locator.
/// </summary>
[PublicAPI]
public interface IGameLocator
{
    /// <summary>
    /// Locates all registered games.
    /// </summary>
    IEnumerable<GameLocatorResult> Locate();

    /// <summary>
    /// Locates all registered games and reports what it could not resolve cleanly to <paramref name="problems"/>.
    /// A locator with nothing to report can leave this alone.
    /// </summary>
    IEnumerable<GameLocatorResult> Locate(IGameLocatorProblemSink problems) => Locate();

    /// <summary>
    /// Tries to locate a single game.
    /// </summary>
    bool TryLocate(IGameData game, [NotNullWhen(true)] out GameLocatorResult? locatorResult)
    {
        return Locate().TryGetFirst(result => result.Game.GameId == game.GameId, out locatorResult);
    }
}
