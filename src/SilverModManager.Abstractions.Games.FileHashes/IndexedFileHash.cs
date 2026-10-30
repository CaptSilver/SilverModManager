using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Games.FileHashes;

/// <summary>
/// A game file that was read in full and found unchanged by a second stat, with the fingerprint it had.
/// </summary>
/// <param name="Identity">The file's xxHash3 identity; the row stores its digest.</param>
/// <param name="Hashes">
/// Every hash of the bytes, set when the read computed them all (a read of a Game-location file). They are
/// recorded as a <c>HashRelation</c> so a later lookup can join an xxHash3 to the file's other digests.
/// </param>
public readonly record struct IndexedFileHash(GamePath Path, Size Size, DateTimeOffset ModifiedUtc, FileIdentity Identity, MultiHash? Hashes = null);
