using System.Collections.Immutable;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Backend.Tests.Games;

internal sealed class CollectingProblemSink : IGameLocatorProblemSink
{
    private readonly List<GameLocatorProblem> _problems = [];

    public ImmutableArray<GameLocatorProblem> Problems => [.. _problems];

    public void Report(string locator, string message) => _problems.Add(new GameLocatorProblem(locator, message));
}
