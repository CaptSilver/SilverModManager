using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.MnemonicDB.Abstractions.Query;
using NexusMods.Hashing.xxHash3;
using System.Runtime.InteropServices;
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

    public IFileIdentityResolver Scoped() => new Memo(this, null);

    public IFileIdentityResolver Scoped(IEnumerable<FileIdentity> expected) => new Memo(this, Prefetch(expected));

    /// <summary>
    /// Below this many distinct identities of one hash kind a pass asks the index about each one, which costs less
    /// than reading the kind's whole attribute.
    /// </summary>
    internal const int ScanFrom = 32;

    private static readonly HashKind[] ScannableKinds = [HashKind.XxHash3, HashKind.Sha1, HashKind.Md5, HashKind.XxHash64, HashKind.Crc32];

    /// <summary>
    /// Reads each hash kind's attribute once per database and notes which of the expected identities any row
    /// records. A pass whose identities have no rows, a tree of files nothing recorded, then asks the index nothing
    /// more, where one question per identity would allocate more than the tree itself.
    /// </summary>
    private Presence? Prefetch(IEnumerable<FileIdentity> expected)
    {
        var byKind = new List<FileIdentity>?[Presence.KindCount];
        foreach (var identity in expected)
        {
            if (!IsScannable(identity.Kind)) continue;
            (byKind[(int)identity.Kind] ??= []).Add(identity);
        }

        Presence? presence = null;
        foreach (var kind in ScannableKinds)
        {
            if (byKind[(int)kind] is not { } list) continue;
            var distinct = Distinct(list);
            if (distinct.Length < ScanFrom) continue;
            (presence ??= new Presence()).Add(kind, distinct);
        }

        if (presence is null) return null;
        foreach (var db in fileHashes.Databases)
        {
            foreach (var kind in ScannableKinds) presence.Scan(db, kind, ref _queriesIssued);
        }

        return presence;
    }

    private static bool IsScannable(HashKind kind) => kind is HashKind.XxHash3 or HashKind.Sha1 or HashKind.Md5 or HashKind.XxHash64 or HashKind.Crc32;

    private static FileIdentity[] Distinct(List<FileIdentity> identities)
    {
        var span = CollectionsMarshal.AsSpan(identities);
        span.Sort(DigestOrder.Instance);
        var count = 0;
        foreach (var identity in span)
        {
            if (count > 0 && DigestOrder.Instance.Compare(span[count - 1], identity) == 0) continue;
            span[count++] = identity;
        }

        return span[..count].ToArray();
    }

    private sealed class DigestOrder : IComparer<FileIdentity>
    {
        public static readonly DigestOrder Instance = new();

        public int Compare(FileIdentity x, FileIdentity y) => x.Digest.SequenceCompareTo(y.Digest);
    }

    /// <summary>
    /// The identities a pass expected to ask about, per hash kind, and whether any database records each. An identity
    /// that is expected and unrecorded has no rows to find, so no question about it needs the index.
    /// </summary>
    private sealed class Presence
    {
        public const int KindCount = 7;

        private readonly FileIdentity[]?[] _expected = new FileIdentity[]?[KindCount];
        private readonly bool[]?[] _recorded = new bool[]?[KindCount];

        public void Add(HashKind kind, FileIdentity[] distinct)
        {
            _expected[(int)kind] = distinct;
            _recorded[(int)kind] = new bool[distinct.Length];
        }

        public void Scan(IDb db, HashKind kind, ref int queries)
        {
            if (_expected[(int)kind] is not { } expected) return;
            var recorded = _recorded[(int)kind];
            if (recorded is null) return;

            Interlocked.Increment(ref queries);
            using var datoms = db.LightweightDatoms(SliceDescriptor.Create(AttributeOf(kind), db.AttributeCache));
            while (datoms.MoveNext())
            {
                if (!TryRead(kind, datoms.ValueSpan, out var identity)) continue;
                var at = Array.BinarySearch(expected, identity, DigestOrder.Instance);
                if (at >= 0) recorded[at] = true;
            }
        }

        public bool IsUnrecorded(FileIdentity identity)
        {
            var kind = (int)identity.Kind;
            if (kind >= KindCount || _expected[kind] is not { } expected || _recorded[kind] is not { } recorded) return false;
            var at = Array.BinarySearch(expected, identity, DigestOrder.Instance);
            return at >= 0 && !recorded[at];
        }

        private static IAttribute AttributeOf(HashKind kind) => kind switch
        {
            HashKind.XxHash3 => HashRelation.XxHash3,
            HashKind.Sha1 => HashRelation.Sha1,
            HashKind.Md5 => HashRelation.Md5,
            HashKind.XxHash64 => HashRelation.XxHash64,
            HashKind.Crc32 => HashRelation.Crc32,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No recorded attribute holds this kind"),
        };

        private static bool TryRead(HashKind kind, ReadOnlySpan<byte> value, out FileIdentity identity)
        {
            switch (kind)
            {
                case HashKind.XxHash3 when value.Length == sizeof(ulong):
                    identity = FileIdentity.XxHash3(Hash.From(MemoryMarshal.Read<ulong>(value)));
                    return true;
                case HashKind.XxHash64 when value.Length == sizeof(ulong):
                    identity = FileIdentity.XxHash64(Hash.From(MemoryMarshal.Read<ulong>(value)));
                    return true;
                case HashKind.Sha1 when value.Length == 20:
                    identity = FileIdentity.Sha1(Sha1Value.From(value));
                    return true;
                case HashKind.Md5 when value.Length == 16:
                    identity = FileIdentity.Md5(Md5Value.From(MemoryMarshal.Read<UInt128>(value)));
                    return true;
                case HashKind.Crc32 when value.Length == sizeof(uint):
                    identity = FileIdentity.Crc32(Crc32Value.From(MemoryMarshal.Read<uint>(value)));
                    return true;
                default:
                    identity = default;
                    return false;
            }
        }
    }

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
        // Rows can share a digest and differ in another, so the first row of one direction may name other bytes
        // while a row of the other direction joins the pair. Either direction joining them makes them the same.
        var knownDifferent = false;
        if (source.TryConvert(other, size, HashKind.XxHash3, out var otherAsXxHash3))
        {
            if (xxHash3.MatchSameKind(otherAsXxHash3) == IdentityMatch.Same) return IdentityMatch.Same;
            knownDifferent = true;
        }

        if (source.TryConvert(xxHash3, size, other.Kind, out var xxHash3AsOther))
        {
            if (xxHash3AsOther.MatchSameKind(other) == IdentityMatch.Same) return IdentityMatch.Same;
            knownDifferent = true;
        }

        return knownDifferent ? IdentityMatch.Different : IdentityMatch.Unknown;
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

    /// <summary>
    /// How many index queries this resolver has put to the hash databases, for tests that bound the cost of a pass.
    /// </summary>
    internal int QueriesIssued => Volatile.Read(ref _queriesIssued);

    private int _queriesIssued;

    private IEnumerable<HashRelation.ReadOnly> RowsOf(IDb db, FileIdentity identity)
    {
        Interlocked.Increment(ref _queriesIssued);
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

    private sealed class Memo(IFileIdentityResolver inner, Presence? presence) : IFileIdentityResolver
    {
        private readonly Dictionary<(FileIdentity Identity, Size Size, HashKind Kind), FileIdentity?> _converted = [];

        public bool TryConvert(FileIdentity identity, Size size, HashKind kind, out FileIdentity converted)
        {
            if (identity.Kind == kind)
            {
                converted = identity;
                return true;
            }

            if (presence?.IsUnrecorded(identity) == true)
            {
                converted = default;
                return false;
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

        public IEnumerable<FileIdentity> StoreKeyCandidates(FileIdentity identity, Size size)
            => identity.Kind != HashKind.XxHash3 && presence?.IsUnrecorded(identity) == true ? [] : inner.StoreKeyCandidates(identity, size);

        public IFileIdentityResolver Scoped() => this;

        public IFileIdentityResolver Scoped(IEnumerable<FileIdentity> expected) => this;
    }
}
