using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NexusMods.Hashing.xxHash3;

namespace SilverModManager.Sdk.Hashes;

/// <summary>
/// A hash together with the algorithm that produced it. Two identities are equal only when the kind, the length and
/// every digest byte agree; an xxHash3 and a SHA-1 of the same bytes are different identities.
/// There is no conversion to or from <see cref="Hash"/>: callers that need an xxHash3 say so with <c>XxHash3()</c>
/// (throws) or <see cref="TryXxHash3"/>, so a SHA-1 can never reach the file store or a loadout file by accident.
/// </summary>
public readonly struct FileIdentity : IEquatable<FileIdentity>
{
    /// <summary>
    /// The longest digest an identity holds (SHA-256). A longer kind raises this constant, which is code, not schema.
    /// </summary>
    public const int MaxDigestLength = 32;

    private readonly byte _kind;
    private readonly byte _length;
    private readonly DigestBuffer _digest;

    private FileIdentity(HashKind kind, ReadOnlySpan<byte> digest)
    {
        _kind = (byte)kind;
        _length = (byte)digest.Length;
        _digest = default;
        digest.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<DigestBuffer, byte>(ref _digest), MaxDigestLength));
    }

    /// <summary>The algorithm of <see cref="Digest"/>.</summary>
    public HashKind Kind => (HashKind)_kind;

    /// <summary>The digest bytes.</summary>
    public ReadOnlySpan<byte> Digest => MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<DigestBuffer, byte>(ref Unsafe.AsRef(in _digest)), _length);

    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => Kind == HashKind.None;

    /// <summary>No identity. Equal only to itself.</summary>
    public static FileIdentity None => default;

    /// <summary>
    /// An xxHash3 identity: eight big-endian bytes, so SQL can build the same bytes from the integer.
    /// </summary>
    public static FileIdentity XxHash3(Hash hash)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, hash.Value);
        return new FileIdentity(HashKind.XxHash3, bytes);
    }

    /// <summary>
    /// A SHA-1 identity, in the byte order <see cref="Sha1Value.AsSpan"/> yields (what <see cref="Sha1Attribute"/> stores).
    /// </summary>
    public static FileIdentity Sha1(Sha1Value sha1) => new(HashKind.Sha1, sha1.AsSpan());

    /// <summary>
    /// An identity of any kind, including one this build has no factory for.
    /// </summary>
    public static FileIdentity Of(HashKind kind, ReadOnlySpan<byte> digest)
    {
        if (kind == HashKind.None) throw new ArgumentOutOfRangeException(nameof(kind), kind, "None carries no digest");
        ArgumentOutOfRangeException.ThrowIfZero(digest.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(digest.Length, MaxDigestLength);
        return new FileIdentity(kind, digest);
    }

    /// <summary>
    /// The digest of <paramref name="kind"/> out of a <see cref="MultiHash"/>. Throws for a kind the MultiHash does not carry.
    /// </summary>
    public static FileIdentity Of(MultiHash hashes, HashKind kind)
    {
        switch (kind)
        {
            case HashKind.XxHash3: return XxHash3(hashes.XxHash3);
            case HashKind.Sha1: return Sha1(hashes.Sha1);
            case HashKind.Md5: return new FileIdentity(kind, hashes.Md5.AsSpan());
            case HashKind.XxHash64:
            {
                Span<byte> bytes = stackalloc byte[sizeof(ulong)];
                BinaryPrimitives.WriteUInt64BigEndian(bytes, hashes.XxHash64.Value);
                return new FileIdentity(kind, bytes);
            }
            case HashKind.Crc32:
            {
                Span<byte> bytes = stackalloc byte[sizeof(uint)];
                BinaryPrimitives.WriteUInt32BigEndian(bytes, hashes.Crc32.Value);
                return new FileIdentity(kind, bytes);
            }
            default:
                throw new InvalidOperationException($"A MultiHash has no {kind} digest");
        }
    }

    /// <summary>
    /// The xxHash3 of an xxHash3 identity. Throws for any other kind: nothing keyed by xxHash3 may be handed a digest of another algorithm.
    /// </summary>
    public Hash XxHash3() => TryXxHash3(out var hash) ? hash : throw new InvalidOperationException($"{this} is a {Kind} identity, not an xxHash3");

    /// <summary>
    /// The xxHash3 of an xxHash3 identity, or false for any other kind.
    /// </summary>
    public bool TryXxHash3(out Hash hash)
    {
        if (Kind == HashKind.XxHash3 && _length == sizeof(ulong))
        {
            hash = Hash.From(BinaryPrimitives.ReadUInt64BigEndian(Digest));
            return true;
        }

        hash = default;
        return false;
    }

    /// <summary>
    /// Writes the kind byte then the digest to <paramref name="destination"/>, returning the byte count.
    /// </summary>
    public int Write(Span<byte> destination)
    {
        destination[0] = _kind;
        Digest.CopyTo(destination[1..]);
        return 1 + _length;
    }

    /// <summary>
    /// The inverse of <see cref="Write"/>. A kind byte this build does not know is kept as that <see cref="HashKind"/> value.
    /// </summary>
    public static FileIdentity Read(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length == 0 || encoded[0] == (byte)HashKind.None) return None;
        return Of((HashKind)encoded[0], encoded[1..]);
    }

    /// <summary>
    /// Uppercase hex of the kind byte followed by the digest (<c>01</c> + 16 digits for xxHash3). The form SQL builds.
    /// </summary>
    public string ToHex()
    {
        Span<byte> buffer = stackalloc byte[1 + MaxDigestLength];
        var written = Write(buffer);
        return Convert.ToHexString(buffer[..written]);
    }

    /// <summary>
    /// The inverse of <see cref="ToHex"/>; case-insensitive because SQL and C# need not agree on casing.
    /// </summary>
    public static FileIdentity ParseHex(ReadOnlySpan<char> hex) => Read(Convert.FromHexString(hex));

    /// <inheritdoc/>
    public bool Equals(FileIdentity other) => _kind == other._kind && Digest.SequenceEqual(other.Digest);

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is FileIdentity other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_kind);
        hash.AddBytes(Digest);
        return hash.ToHashCode();
    }

    /// <summary>For logs: <c>xxh3:…</c>, <c>sha1:…</c>, <c>kind7:…</c>.</summary>
    public override string ToString() => Kind switch
    {
        HashKind.None => "none",
        HashKind.XxHash3 => $"xxh3:{Convert.ToHexString(Digest)}",
        HashKind.Sha1 => $"sha1:{Convert.ToHexString(Digest)}",
        HashKind.Md5 => $"md5:{Convert.ToHexString(Digest)}",
        HashKind.Sha256 => $"sha256:{Convert.ToHexString(Digest)}",
        HashKind.XxHash64 => $"xxh64:{Convert.ToHexString(Digest)}",
        HashKind.Crc32 => $"crc32:{Convert.ToHexString(Digest)}",
        _ => $"kind{_kind}:{Convert.ToHexString(Digest)}",
    };

    /// <summary/>
    public static bool operator ==(FileIdentity left, FileIdentity right) => left.Equals(right);

    /// <summary/>
    public static bool operator !=(FileIdentity left, FileIdentity right) => !left.Equals(right);

    [InlineArray(MaxDigestLength)]
    private struct DigestBuffer
    {
        private byte _value;
    }
}
