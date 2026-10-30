using SilverModManager.Sdk.Hashes;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Files;


/// <summary>
/// Interface for things that want to be put into a file tree
/// </summary>
public interface IFileTreeNode
{
    /// <summary>
    /// Path of the file
    /// </summary>
    GamePath To { get; }
    
    /// <summary>
    /// Identity of the file
    /// </summary>
    FileIdentity Identity { get; }
    
    /// <summary>
    /// Size of the file
    /// </summary>
    Size Size { get; }
}
