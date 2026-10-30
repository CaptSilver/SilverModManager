using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Sdk.FileStore;

/// <summary>
/// Exception for missing archives in the file store.
/// </summary>
public class MissingArchiveException : Exception
{
    /// <summary>
    /// Constructor.
    /// </summary>
    public MissingArchiveException(FileIdentity identity) : base($"Missing archive for {identity.ToHex()}") { }
    
    /// <summary>
    /// Constructor with an absolute path that specifies where the file would be extracted to
    /// </summary>
    public MissingArchiveException(FileIdentity identity, AbsolutePath path) : base($"Missing archive for {identity.ToHex()} intended for output path {path}") { }
}


