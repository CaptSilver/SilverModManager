using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Games.FileHashes;

/// <summary>
/// A struct that contains information about a file in a game
/// </summary>
public record struct GameFileRecord
{
    /// <summary>
    /// The path to the file
    /// </summary>
    public required GamePath Path { get; init; }
    
    /// <summary>
    /// The size of the game file
    /// </summary>
    public required Size Size { get; init; }
    
    /// <summary>
    /// What identifies the game file: the full hash its store row holds, tagged with the algorithm.
    /// </summary>
    public required FileIdentity Identity { get; init; }
}
