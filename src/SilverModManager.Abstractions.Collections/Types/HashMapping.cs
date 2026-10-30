using SilverModManager.Sdk.Hashes;
using NexusMods.Paths;

namespace SilverModManager.Abstractions.Collections.Types;

/// <summary>
/// Metadata about a mapping from a MD5 hash to a xxHash64 hash and the size of the file.
/// </summary>
public struct HashMapping
{
    /// <summary>
    /// The identity of the file in the file store (an xxHash3).
    /// </summary>
    public required FileIdentity Identity { get; init; }
    
    /// <summary>
    /// The size of the file.
    /// </summary>
    public required Size Size { get; init; }
}
