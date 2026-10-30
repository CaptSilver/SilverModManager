using NexusMods.Paths;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Games.FileHashes;

/// <summary>
/// Reads and writes <see cref="LocalFileHash"/> rows. Shared so every <see cref="IFileHashesService"/> implementation keys them the same way.
/// </summary>
public static class LocalFileHashRows
{
    /// <summary>
    /// The row for exactly this location and path. <see cref="LocalFileHash.Path"/> compares case-insensitively, so
    /// <c>Mods/x</c> and <c>mods/x</c> come back together from the index and the spelling is compared here.
    /// </summary>
    public static IEnumerable<LocalFileHash.ReadOnly> Find(IDb db, string gamePath, GamePath path)
    {
        foreach (var row in LocalFileHash.FindByPath(db, path.Path))
        {
            if (row.GamePath != gamePath || row.Location != path.LocationId) continue;
            if (!string.Equals(row.Path.Path, path.Path.Path, StringComparison.Ordinal)) continue;
            yield return row;
        }
    }

    /// <summary>
    /// Replaces the rows of the given files in <paramref name="tx"/>. A file that carries all its digests also gets a
    /// bare <see cref="HashRelation"/>: that is what lets a later baseline resolve the file from its hashes instead of
    /// reading it, and what joins a SHA-1 to the xxHash3 the file is archived under.
    /// A caller that already made relations in <paramref name="tx"/> passes them as <paramref name="relations"/>, so the
    /// same bytes do not get a second row.
    /// </summary>
    public static void Record(ITransaction tx, IDb db, string gamePath, IEnumerable<IndexedFileHash> files, Dictionary<FileIdentity, HashRelationId>? relations = null)
    {
        relations ??= new Dictionary<FileIdentity, HashRelationId>();
        foreach (var file in files)
        {
            if (file.Hashes is { } hashes) _ = EnsureHashRelation(tx, db, relations, hashes);
            foreach (var old in Find(db, gamePath, file.Path)) tx.Delete(old.Id, recursive: false);
            _ = new LocalFileHash.New(tx)
            {
                GamePath = gamePath,
                Location = file.Path.LocationId,
                Path = file.Path.Path,
                Size = file.Size,
                ModifiedUtc = file.ModifiedUtc,
                XxHash3 = file.Identity.XxHash3(),
            };
        }
    }

    /// <summary>
    /// The stored identity (always an xxHash3) when a row has this exact size and modified instant.
    /// </summary>
    public static bool TryGet(IDb db, string gamePath, GamePath path, Size size, DateTimeOffset modifiedUtc, out FileIdentity identity)
    {
        foreach (var row in Find(db, gamePath, path))
        {
            if (row.Size != size || row.ModifiedUtc.UtcTicks != modifiedUtc.UtcTicks) continue;
            identity = FileIdentity.XxHash3(row.XxHash3);
            return true;
        }

        identity = default;
        return false;
    }

    /// <summary>
    /// The relation for these bytes: the existing one, one made earlier in the same transaction (<paramref name="created"/>),
    /// or a new one added to <paramref name="tx"/>.
    /// </summary>
    public static HashRelationId EnsureHashRelation(ITransaction tx, IDb db, Dictionary<FileIdentity, HashRelationId> created, MultiHash hash)
    {
        var key = FileIdentity.XxHash3(hash.XxHash3);
        if (created.TryGetValue(key, out var known)) return known;

        var existing = HashRelation.FindByXxHash3(db, hash.XxHash3).FirstOrDefault();
        if (existing.IsValid()) return created[key] = existing.HashRelationId;

        var relation = new HashRelation.New(tx)
        {
            MinimalHash = hash.MinimalHash,
            XxHash3 = hash.XxHash3,
            Sha1 = hash.Sha1,
            XxHash64 = hash.XxHash64,
            Md5 = hash.Md5,
            Size = hash.Size,
            Crc32 = hash.Crc32,
        };
        return created[key] = relation.Id;
    }
}
