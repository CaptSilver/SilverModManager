using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Sdk.FileStore;

/// <summary>
/// A stream source that can also find bytes by a <see cref="FileIdentity"/> of any kind, for the sources that
/// know files by something other than their xxHash3 (a game file on disk known by its depot SHA-1).
/// </summary>
public interface IIdentityStreamSource : IReadOnlyStreamSource;
