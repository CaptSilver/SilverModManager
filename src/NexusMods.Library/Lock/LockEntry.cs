using JetBrains.Annotations;

namespace NexusMods.Library.Lock;

/// <summary>
/// One pinned mod in a lock file: which version it is, which file of the source it is, and the SHA-256 the bytes
/// must have.
/// </summary>
/// <param name="Id">The table name in the lock file.</param>
/// <param name="FileId">The Nexus Mods file id; only Nexus Mods entries have one.</param>
/// <param name="Asset">The file name of the archive.</param>
/// <param name="Sha256">The expected digest as the lock file spells it. It may be empty or malformed; those entries never verify.</param>
/// <param name="Source">Either <see cref="LockSources.Nexus"/> or <see cref="LockSources.GitHub"/>.</param>
[PublicAPI]
public sealed record LockEntry(string Id, string Version, string? FileId, string Asset, string Sha256, string Source);

/// <summary>
/// The values of <see cref="LockEntry.Source"/>.
/// </summary>
[PublicAPI]
public static class LockSources
{
    public const string Nexus = "nexus";
    public const string GitHub = "github";
}

/// <summary>
/// A lock file could not be read, or a loadout could not be turned into one.
/// </summary>
[PublicAPI]
public sealed class LockException(string message) : Exception(message);
