using JetBrains.Annotations;
using NexusMods.Paths;

namespace NexusMods.Sdk.Games;

/// <summary>
/// A game whose executable is shared with another game, and so needs more than that file to be recognised in a
/// folder the user points at.
/// </summary>
[PublicAPI]
public interface IGameIdentifyingFiles
{
    /// <summary>
    /// Files, relative to the game folder, that exist in this game's install and not in the install of another game
    /// that launches the same executable. All of them have to be present for the folder to count as this game.
    /// </summary>
    IReadOnlyList<RelativePath> IdentifyingFiles { get; }
}
