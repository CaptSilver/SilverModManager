using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// A interface used by structs that have a hash, size and reference, such as LoadoutFiles, and DiskStateEntries.
/// </summary>
public interface IHavePathHashSizeAndReference
{
    /// <summary>
    /// The path of the file.
    /// </summary>
    public GamePath Path { get; }

    /// <summary>
    /// The identity of the file's bytes.
    /// </summary>
    public FileIdentity Identity { get; }
    
    /// <summary>
    /// The size of the file.
    /// </summary>
    public Size Size { get; }
    
    /// <summary>
    /// The reference to the file.
    /// </summary>
    public EntityId Reference { get; }
}
