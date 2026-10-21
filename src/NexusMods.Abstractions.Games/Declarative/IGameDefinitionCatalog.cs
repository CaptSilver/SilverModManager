using System.Collections.Immutable;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Games.Declarative;

/// <summary>
/// The games described by YAML definitions, as far as the code that adds games by path needs to know.
/// It lives here so projects that reference <c>Abstractions.Games</c> don't need a reference to the
/// project that implements it.
/// </summary>
public interface IGameDefinitionCatalog
{
    /// <summary>
    /// The games, definitions and coded ones, whose primary executable exists inside <paramref name="folder"/>.
    /// Several games can share an executable (Skyrim Special Edition and Enderal Special Edition both use
    /// <c>SkyrimSE.exe</c>), so the caller picks when more than one comes back. Empty when nothing matches.
    /// </summary>
    ImmutableArray<GameId> MatchFolder(AbsolutePath folder);

    /// <summary>
    /// Writes a definition with no store ids to the user's definitions folder, so a game that has no
    /// definition yet can be added by path, and returns its id.
    /// </summary>
    /// <remarks>
    /// The id is a slug of <paramref name="name"/>; <c>slug-2</c>, <c>slug-3</c>, ... when that id is already
    /// taken by a definition, a coded game or a file in the folder.
    /// </remarks>
    /// <param name="name">The name shown for the game.</param>
    /// <param name="primaryExe">The game's executable, relative to its install folder.</param>
    GameId WriteMinimalUserDefinition(string name, RelativePath primaryExe);
}
