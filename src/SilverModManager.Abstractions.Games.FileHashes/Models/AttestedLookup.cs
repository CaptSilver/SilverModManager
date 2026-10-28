using NexusMods.Paths;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Games.FileHashes.Models;

/// <summary>
/// One <see cref="AttestedFile"/> row, detached from the database it was read from.
/// </summary>
public readonly record struct AttestedLookup(EntityId Id, RelativePath Path, Sha1Value Sha1, Size Size, DateTimeOffset ModifiedUtc);
