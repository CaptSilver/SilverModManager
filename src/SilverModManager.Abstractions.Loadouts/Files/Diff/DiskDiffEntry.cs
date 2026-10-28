using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Loadouts.Files.Diff;

/// <summary>
/// A file diff entry
/// </summary>
public record DiskDiffEntry
{
    /// <summary>
    /// The game path of the file
    /// </summary>
    public required GamePath GamePath { get; init; }
    
    /// <summary>
    /// The size of the file (newest if modified)
    /// </summary>
    public required Size Size { get; init; }
    
    /// <summary>
    /// The identity of the file (newest if modified)
    /// </summary>
    public required FileIdentity Identity { get; init; }
    
    /// <summary>
    /// The type of change that occurred to the file path
    /// </summary>
    public required FileChangeType ChangeType { get; init; }
}
