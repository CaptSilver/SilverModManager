using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Abstractions.Games.FileHashes;

/// <summary>
/// Reads and settles <see cref="AttestedFile"/> rows. Shared so every <see cref="IFileHashesService"/> implementation
/// treats them the same way.
/// </summary>
public static class AttestedFileRows
{
    /// <summary>
    /// The rows of the baseline at <paramref name="gamePath"/> for <paramref name="store"/>, keyed by their path in
    /// the game folder. Empty when there is no such baseline, or when it was built for other
    /// <paramref name="locatorIds"/>: after a game update its rows describe files the new build replaced, and
    /// the new build's own manifest answers for every path.
    /// </summary>
    public static FrozenDictionary<GamePath, AttestedLookup> Find(IDb db, GameStore store, string gamePath, IEnumerable<LocatorId> locatorIds)
    {
        var baseline = LocalBaseline.FindByGamePath(db, gamePath).Where(b => b.Store == store).Select(b => (LocalBaseline.ReadOnly?)b).FirstOrDefault();
        if (baseline is not { } found) return FrozenDictionary<GamePath, AttestedLookup>.Empty;

        var current = locatorIds.Select(id => id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!current.SetEquals(found.LocatorIds)) return FrozenDictionary<GamePath, AttestedLookup>.Empty;

        var rows = new Dictionary<GamePath, AttestedLookup>();
        foreach (var row in AttestedFile.FindByBaseline(db, found.Id))
            rows[new GamePath(LocationId.Game, row.Path)] = new AttestedLookup(row.Id, row.Path, row.Sha1, row.Size, row.ModifiedUtc);
        return rows.ToFrozenDictionary();
    }

    /// <summary>
    /// Records what reading attested files found, in one transaction on <paramref name="local"/>; see
    /// <see cref="IFileHashesService.SettleAttested"/>.
    /// </summary>
    public static async Task Settle(IConnection local, string gamePath, IReadOnlyList<(AttestedLookup Attested, MultiHash Hashes, FileFingerprint After)> read, ILogger logger)
    {
        if (read.Count == 0) return;

        var db = local.Db;
        var relations = new Dictionary<Hash, HashRelationId>();
        var fingerprints = new List<IndexedFileHash>(read.Count);

        using var tx = local.BeginTransaction();
        foreach (var (attested, hashes, after) in read)
        {
            var row = AttestedFile.Load(db, attested.Id);
            var matches = row.IsValid() && hashes.Sha1 == attested.Sha1 && hashes.Size == attested.Size;
            if (row.IsValid() && !matches)
                logger.LogInformation("`{Path}` no longer matches the store's copy; it is treated as an external change", attested.Path);

            // The fingerprint row and the bytes' relation are written whatever the row's fate, so a baseline rebuilt
            // since finds the bytes by their SHA-1 and resolves the path instead of attesting it again. A promotion
            // writes the relation itself, below, so the fingerprint row must not add a second one.
            fingerprints.Add(new IndexedFileHash(new GamePath(LocationId.Game, attested.Path), after.Size, after.LastModified, hashes.XxHash3, matches ? null : hashes));
            if (!matches) continue;

            var relation = LocalFileHashRows.EnsureHashRelation(tx, db, relations, hashes);
            var pathRelation = new PathHashRelation.New(tx) { Path = attested.Path, HashId = relation };
            tx.Add(row.BaselineId, LocalBaseline.Files, pathRelation.Id);
            tx.Add(row.ManifestId, SteamManifest.Files, pathRelation.Id);
            tx.Delete(row.Id, recursive: false);
        }

        LocalFileHashRows.Record(tx, db, gamePath, fingerprints);
        await tx.Commit();
    }
}
