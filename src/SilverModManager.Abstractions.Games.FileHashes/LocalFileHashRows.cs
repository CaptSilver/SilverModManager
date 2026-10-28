using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;

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
    /// Replaces the rows of the given files in <paramref name="tx"/>.
    /// </summary>
    public static void Record(ITransaction tx, IDb db, string gamePath, IEnumerable<IndexedFileHash> files)
    {
        foreach (var file in files)
        {
            foreach (var old in Find(db, gamePath, file.Path)) tx.Delete(old.Id, recursive: false);
            _ = new LocalFileHash.New(tx)
            {
                GamePath = gamePath,
                Location = file.Path.LocationId,
                Path = file.Path.Path,
                Size = file.Size,
                ModifiedUtc = file.ModifiedUtc,
                XxHash3 = file.XxHash3,
            };
        }
    }

    /// <summary>
    /// The stored hash when a row has this exact size and modified instant.
    /// </summary>
    public static bool TryGet(IDb db, string gamePath, GamePath path, Size size, DateTimeOffset modifiedUtc, out Hash xxHash3)
    {
        foreach (var row in Find(db, gamePath, path))
        {
            if (row.Size != size || row.ModifiedUtc.UtcTicks != modifiedUtc.UtcTicks) continue;
            xxHash3 = row.XxHash3;
            return true;
        }

        xxHash3 = default;
        return false;
    }
}
