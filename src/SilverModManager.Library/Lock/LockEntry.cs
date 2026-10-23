using JetBrains.Annotations;
using SilverModManager.Library.Sources;

namespace SilverModManager.Library.Lock;

/// <summary>
/// One pinned mod in a lock file: which version it is, which file of the source it is, and the SHA-256 the bytes
/// must have.
/// </summary>
/// <param name="Id">The table name in the lock file.</param>
/// <param name="FileId">The Nexus Mods file id; only Nexus Mods entries have one.</param>
/// <param name="Asset">The file name of the archive.</param>
/// <param name="Sha256">The expected digest as the lock file spells it. It may be empty or malformed; those entries never verify.</param>
/// <param name="Source">One of <see cref="LockSources.All"/>; the <see cref="ILibrarySource.Name"/> of the source that fetches it.</param>
[PublicAPI]
public sealed record LockEntry(string Id, string Version, string? FileId, string Asset, string Sha256, string Source)
{
    internal LibrarySourceEntry ToSourceEntry() => new(Id, Version, FileId, Asset, Sha256, Source);

    internal static LockEntry From(LibrarySourceEntry entry) => new(entry.Id, entry.Version, entry.FileId, entry.Asset, entry.Sha256, entry.Source);
}

/// <summary>
/// The values of <see cref="LockEntry.Source"/>.
/// </summary>
[PublicAPI]
public static class LockSources
{
    public const string Nexus = "nexus";
    public const string GitHub = "github";

    /// <summary>
    /// Not a source erm knows: a lock that uses it is ours alone, and erm will refuse it.
    /// </summary>
    public const string Thunderstore = "thunderstore";

    /// <summary>
    /// Every source a lock file may name.
    /// </summary>
    public static readonly IReadOnlyList<string> All = [Nexus, GitHub, Thunderstore];
}

/// <summary>
/// A lock file could not be read, or a loadout could not be turned into one.
/// </summary>
[PublicAPI]
public sealed class LockException(string message) : Exception(message);
