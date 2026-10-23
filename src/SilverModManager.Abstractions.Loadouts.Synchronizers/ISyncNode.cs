using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// A node in the synchronization tree.
/// </summary>
public interface ISyncNode
{
    /// <summary>
    /// The actions that can be performed on this node.
    /// </summary>
    public Actions Actions { get; set; }

    /// <summary>
    /// The path of the file in the game folder.
    /// </summary>
    public GamePath Path { get; }
}
