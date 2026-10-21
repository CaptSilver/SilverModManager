using JetBrains.Annotations;
using NexusMods.Abstractions.Games;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.TestFramework;

/// <summary>
/// Base class for tests of a game that comes from a YAML definition. The installation is the one
/// whose game has the definition's id, since a definition has no class of its own to test for.
/// </summary>
[PublicAPI]
public abstract class ADeclarativeGameTest : AGameTest<IGame>
{
    protected ADeclarativeGameTest(IServiceProvider serviceProvider, string definitionId)
        : base(serviceProvider, GameId.From(definitionId))
    {
    }

    /// <param name="installationFilter">Picks among several installs of the definition, by folder for instance.</param>
    protected ADeclarativeGameTest(IServiceProvider serviceProvider, string definitionId, Func<GameInstallation, bool> installationFilter)
        : base(serviceProvider, installation => installation.Game.GameId == GameId.From(definitionId) && installationFilter(installation))
    {
    }
}
