using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>Finds the <see cref="IMergeEngine"/> that serves a game.</summary>
public sealed class MergeEngines(IEnumerable<IMergeEngine> engines)
{
    private readonly IMergeEngine[] _engines = engines.ToArray();

    /// <summary>The engine for <paramref name="game"/>; null when no engine merges its files.</summary>
    /// <exception cref="InvalidOperationException">Two engines claim the game, which is a registration mistake.</exception>
    public IMergeEngine? For(IGameData game)
    {
        var serving = _engines.Where(e => e.Serves(game)).ToArray();
        return serving.Length switch
        {
            0 => null,
            1 => serving[0],
            _ => throw new InvalidOperationException($"{game.DisplayName} has more than one merge engine: {string.Join(", ", serving.Select(e => e.GetType().Name))}"),
        };
    }
}
