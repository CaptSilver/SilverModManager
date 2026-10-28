using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.FileHashes;

/// <summary>
/// A game file that was read in full and found unchanged by a second stat, with the fingerprint it had.
/// </summary>
public readonly record struct IndexedFileHash(GamePath Path, Size Size, DateTimeOffset ModifiedUtc, Hash XxHash3);
