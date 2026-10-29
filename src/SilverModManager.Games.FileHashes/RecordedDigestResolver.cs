using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.FileHashes;

/// <summary>
/// Joins file identities of different kinds through the <see cref="HashRelation"/> rows of the local store and
/// then the downloaded one. Caches nothing: the local store changes under it, so use <see cref="Scoped"/> for a
/// pass that asks the same question many times.
/// </summary>
internal sealed class RecordedDigestResolver(IFileHashesService fileHashes) : IFileIdentityResolver
{
    public bool TryConvert(FileIdentity identity, Size size, HashKind kind, out FileIdentity converted)
    {
        if (identity.Kind == kind)
        {
            converted = identity;
            return true;
        }

        foreach (var row in MatchingRows(identity, size))
        {
            if (!Records(row, kind)) continue;
            converted = row.Identity(kind);
            return true;
        }

        converted = default;
        return false;
    }

    public IdentityMatch Resolve(FileIdentity left, FileIdentity right, Size size) => ResolveWith(this, left, right, size);

    public IEnumerable<FileIdentity> StoreKeyCandidates(FileIdentity identity, Size size)
    {
        if (identity.Kind == HashKind.XxHash3)
        {
            yield return identity;
            yield break;
        }

        HashSet<FileIdentity> seen = [];
        foreach (var row in MatchingRows(identity, size))
        {
            if (!Records(row, HashKind.XxHash3)) continue;
            var candidate = row.Identity(HashKind.XxHash3);
            if (seen.Add(candidate)) yield return candidate;
        }
    }

    public IFileIdentityResolver Scoped() => new Memo(this);

    /// <summary>
    /// Takes the converting step from <paramref name="source"/> so a memo can sit under the same decision rule.
    /// </summary>
    private static IdentityMatch ResolveWith(IFileIdentityResolver source, FileIdentity left, FileIdentity right, Size size)
    {
        if (left.Kind == right.Kind) return left.MatchSameKind(right);

        // xxHash3 is the store key and the most widely recorded digest, so meet there when either side has it. A row
        // keyed by the other side's digest may not exist while one keyed by the xxHash3 does, so that side is
        // converted the other way before giving up.
        if (left.Kind == HashKind.XxHash3)
            return Meet(source, other: right, xxHash3: left, size);
        if (right.Kind == HashKind.XxHash3)
            return Meet(source, other: left, xxHash3: right, size);

        if (source.TryConvert(left, size, right.Kind, out var convertedLeft)) return convertedLeft.MatchSameKind(right);
        return source.TryConvert(right, size, left.Kind, out var convertedRight) ? left.MatchSameKind(convertedRight) : IdentityMatch.Unknown;
    }

    private static IdentityMatch Meet(IFileIdentityResolver source, FileIdentity other, FileIdentity xxHash3, Size size)
    {
        if (source.TryConvert(other, size, HashKind.XxHash3, out var otherAsXxHash3)) return xxHash3.MatchSameKind(otherAsXxHash3);
        return source.TryConvert(xxHash3, size, other.Kind, out var xxHash3AsOther) ? xxHash3AsOther.MatchSameKind(other) : IdentityMatch.Unknown;
    }

    private IEnumerable<HashRelation.ReadOnly> MatchingRows(FileIdentity identity, Size size)
    {
        foreach (var db in fileHashes.Databases)
        {
            foreach (var row in RowsOf(db, identity))
            {
                if (row.IsValid() && row.Size == size) yield return row;
            }
        }
    }

    private static IEnumerable<HashRelation.ReadOnly> RowsOf(IDb db, FileIdentity identity)
    {
        switch (identity.Kind)
        {
            case HashKind.XxHash3 when identity.TryXxHash3(out var xxHash3):
                return HashRelation.FindByXxHash3(db, xxHash3);
            case HashKind.Sha1 when identity.TrySha1(out var sha1):
                return HashRelation.FindBySha1(db, sha1);
            case HashKind.XxHash64 when identity.TryXxHash64(out var xxHash64):
                return Load(db, db.Datoms(HashRelation.XxHash64, xxHash64));
            case HashKind.Md5 when identity.TryMd5(out var md5):
                return Load(db, db.Datoms(HashRelation.Md5, md5));
            case HashKind.Crc32 when identity.TryCrc32(out var crc32):
                return Load(db, db.Datoms(HashRelation.Crc32, crc32));
            default:
                return [];
        }
    }

    private static IEnumerable<HashRelation.ReadOnly> Load(IDb db, IndexSegment datoms)
    {
        foreach (var datom in datoms) yield return HashRelation.Load(db, datom.E);
    }

    private static bool Records(HashRelation.ReadOnly row, HashKind kind) => kind switch
    {
        HashKind.XxHash3 => row.Contains(HashRelation.XxHash3),
        HashKind.Sha1 => row.Contains(HashRelation.Sha1),
        HashKind.Md5 => row.Contains(HashRelation.Md5),
        HashKind.XxHash64 => row.Contains(HashRelation.XxHash64),
        HashKind.Crc32 => row.Contains(HashRelation.Crc32),
        _ => false,
    };

    private sealed class Memo(IFileIdentityResolver inner) : IFileIdentityResolver
    {
        private readonly Dictionary<(FileIdentity Identity, Size Size, HashKind Kind), FileIdentity?> _converted = [];

        public bool TryConvert(FileIdentity identity, Size size, HashKind kind, out FileIdentity converted)
        {
            if (identity.Kind == kind)
            {
                converted = identity;
                return true;
            }

            var key = (identity, size, kind);
            if (!_converted.TryGetValue(key, out var cached))
            {
                cached = inner.TryConvert(identity, size, kind, out var found) ? found : null;
                _converted[key] = cached;
            }

            converted = cached.GetValueOrDefault();
            return cached.HasValue;
        }

        public IdentityMatch Resolve(FileIdentity left, FileIdentity right, Size size) => ResolveWith(this, left, right, size);

        public IEnumerable<FileIdentity> StoreKeyCandidates(FileIdentity identity, Size size) => inner.StoreKeyCandidates(identity, size);

        public IFileIdentityResolver Scoped() => this;
    }
}
