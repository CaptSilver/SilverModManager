using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Games.FileHashes.Models;

/// <summary>
/// A <see cref="HashRelation"/> row's digests as <see cref="FileIdentity"/> values.
/// </summary>
public static class HashRelationIdentity
{
    /// <summary>
    /// The row's digest of <paramref name="kind"/>. Throws <see cref="InvalidOperationException"/> when the row does
    /// not record that digest, and <see cref="ArgumentOutOfRangeException"/> for a kind the model never stores
    /// (<see cref="HashKind.None"/>, <see cref="HashKind.Sha256"/>).
    /// </summary>
    public static FileIdentity Identity(this HashRelation.ReadOnly relation, HashKind kind) => kind switch
    {
        HashKind.XxHash3 => FileIdentity.XxHash3(Require(relation, HashRelation.XxHash3, static r => r.XxHash3, kind)),
        HashKind.Sha1 => FileIdentity.Sha1(Require(relation, HashRelation.Sha1, static r => r.Sha1, kind)),
        HashKind.Md5 => FileIdentity.Md5(Require(relation, HashRelation.Md5, static r => r.Md5, kind)),
        HashKind.XxHash64 => FileIdentity.XxHash64(Require(relation, HashRelation.XxHash64, static r => r.XxHash64, kind)),
        HashKind.Crc32 => FileIdentity.Crc32(Require(relation, HashRelation.Crc32, static r => r.Crc32, kind)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A HashRelation row does not store this kind"),
    };

    private static T Require<T>(HashRelation.ReadOnly relation, IAttribute attribute, Func<HashRelation.ReadOnly, T> read, HashKind kind)
        => relation.Contains(attribute) ? read(relation) : throw new InvalidOperationException($"HashRelation {relation.Id} has no {kind} digest");
}
