using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Numerics;
using System.Text;

namespace SilverModManager.Games.FromSoft.Formats;

/// <summary>The bytes of a payload, <paramref name="Start"/> inclusive to <paramref name="End"/> exclusive, that are AES-encrypted.</summary>
public readonly record struct AesRange(long Start, long End);

/// <summary>
/// The index of a packed game archive (<c>.bhd</c>). A game ships almost all of its files inside these, encrypted: the
/// index with RSA, each payload in the <c>.bdt</c> with AES-128-ECB. Reading an index gives the game's own copy of a
/// file, which is the only merge base that cannot lag a patch behind.
/// </summary>
/// <remarks>
/// Outermost first: RSA around the index, then the index itself (buckets of entries keyed by a hash of the path), then
/// the payload with its AES ranges, then whatever the payload is, usually a DCX. The <c>.bdt</c> files run to
/// gigabytes, so nothing here reads one whole.
/// </remarks>
public static class Bhd5
{
    private const int RsaBlock = 256;
    private const int EntrySize = 40;
    private const int KeyLength = AesCbc.BlockSize;

    public sealed record Entry(ulong NameHash, int PaddedSize, int UnpaddedSize, long Offset, byte[]? AesKey, IReadOnlyList<AesRange> Ranges);

    public sealed record Index(string Salt, int BucketCount, IReadOnlyDictionary<ulong, Entry> Entries);

    /// <summary>
    /// The spelling the index hashes: lowercase, forward slashes and a forced leading slash. The slash is the one that
    /// bites: without it every lookup misses and reports a file that is not there, which reads like a genuinely absent
    /// file rather than a normalisation bug.
    /// </summary>
    public static string Normalise(string path)
    {
        var text = path.Trim().Replace('\\', '/').ToLowerInvariant();
        return text.StartsWith('/') ? text : "/" + text;
    }

    /// <summary>
    /// The index hash: <c>h = char + prime * h</c>, accumulated to the game's width. A hash of the wrong width finds
    /// nothing at all, which is the first thing to recheck if an update appears to empty an index.
    /// </summary>
    public static ulong PathHash(string path, FromSoftFormatTraits traits)
    {
        if (traits.PathHashBits is < 1 or > 64)
            throw new Bhd5Exception($"A path hash is 1 to 64 bits wide; this game's is {traits.PathHashBits}");
        var mask = traits.PathHashBits == 64 ? ulong.MaxValue : (1UL << traits.PathHashBits) - 1;
        var prime = (ulong)traits.PathHashPrime;
        var hash = 0UL;
        foreach (var rune in path.EnumerateRunes())
            hash = ((ulong)rune.Value + prime * hash) & mask;
        return hash;
    }

    /// <summary>
    /// RSA-decrypts a <c>.bhd</c>. Each 256-byte ciphertext block yields 255 plaintext bytes, not 256: the encryptor
    /// worked in 255-byte blocks and the result is left-padded to the modulus width, so a correct key always leaves the
    /// top byte zero. Taking 256 per block shifts everything by one and parses into nonsense instead of failing.
    /// </summary>
    public static byte[] DecryptHeader(ReadOnlySpan<byte> blob, ArchiveIndex archive)
    {
        if (blob.IsEmpty || blob.Length % RsaBlock != 0)
            throw new Bhd5Exception($"{archive.Name}.bhd is {blob.Length} bytes, not a whole number of {RsaBlock}-byte RSA blocks: the file is truncated");
        var plain = new byte[blob.Length / RsaBlock * (RsaBlock - 1)];
        Span<byte> block = stackalloc byte[RsaBlock];
        for (var i = 0; i < blob.Length / RsaBlock; i++)
        {
            var cipher = new BigInteger(blob.Slice(i * RsaBlock, RsaBlock), isUnsigned: true, isBigEndian: true);
            var value = BigInteger.ModPow(cipher, archive.Exponent, archive.Modulus);
            block.Clear();
            if (!value.TryWriteBytes(block, out var written, isUnsigned: true, isBigEndian: true))
                throw WrongKey(archive);
            // The big-endian write is minimal; shift it to the right edge to restore the leading zeros.
            block[..written].CopyTo(block[(RsaBlock - written)..]);
            block[..(RsaBlock - written)].Clear();
            // The one integrity signal RSA gives for free. Without it a wrong key yields plausible garbage and every
            // lookup then reports a file that is not in the archive.
            if (block[0] != 0)
                throw WrongKey(archive);
            block[1..].CopyTo(plain.AsSpan(i * (RsaBlock - 1)));
        }
        return plain;
    }

    private static Bhd5Exception WrongKey(ArchiveIndex archive)
        => new($"The RSA key for {archive.Name} did not decrypt it: a game patch may have rotated the key");

    /// <summary>Reads a decrypted index. Only the layouts this reads are accepted; any other is refused by name.</summary>
    public static Index Parse(ReadOnlySpan<byte> plain, FromSoftFormatTraits traits)
    {
        if (traits.ArchiveLayout != Bhd5Layout.Salted40ByteEntries)
            throw new Bhd5Exception($"No reader for the archive layout {traits.ArchiveLayout} ({(int)traits.ArchiveLayout})");
        if (!plain.StartsWith("BHD5"u8))
            throw new Bhd5Exception($"Not a BHD5 index (magic {Convert.ToHexString(plain[..Math.Min(4, plain.Length)])}): the RSA step produced the wrong bytes");

        var bucketCount = ReadInt(plain, 16);
        var bucketOffset = ReadInt(plain, 20);
        var saltLength = ReadInt(plain, 24);
        var salt = Encoding.ASCII.GetString(Slice(plain, 28, saltLength));

        var entries = new Dictionary<ulong, Entry>();
        var position = (long)bucketOffset;
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var count = ReadInt(plain, position);
            var offset = ReadInt(plain, position + 4);
            position += 8;
            for (var i = 0; i < count; i++)
            {
                var at = offset + (long)i * EntrySize;
                var hash = BinaryPrimitives.ReadUInt64LittleEndian(Slice(plain, at, 8));
                var (key, ranges) = ReadKey(plain, BinaryPrimitives.ReadInt64LittleEndian(Slice(plain, at + 32, 8)));
                // A later entry with the same hash replaces the earlier one, as the game's own lookup does.
                entries[hash] = new Entry(hash, ReadInt(plain, at + 8), ReadInt(plain, at + 12),
                    BinaryPrimitives.ReadInt64LittleEndian(Slice(plain, at + 16, 8)), key, ranges);
            }
        }
        return new Index(salt, bucketCount, entries.ToFrozenDictionary());
    }

    /// <summary>The per-file AES key and the ranges it covers, or none: a 16-byte key, an int32 count, then (int64 start, int64 end) pairs where (-1, -1) is unused.</summary>
    private static (byte[]? Key, IReadOnlyList<AesRange> Ranges) ReadKey(ReadOnlySpan<byte> plain, long offset)
    {
        if (offset <= 0 || offset + KeyLength + 4 > plain.Length)
            return (null, []);
        var key = Slice(plain, offset, KeyLength).ToArray();
        var count = ReadInt(plain, offset + KeyLength);
        var ranges = new List<AesRange>();
        var position = offset + KeyLength + 4;
        for (var i = 0; i < count; i++, position += 16)
        {
            var start = BinaryPrimitives.ReadInt64LittleEndian(Slice(plain, position, 8));
            var end = BinaryPrimitives.ReadInt64LittleEndian(Slice(plain, position + 8, 8));
            if (start >= 0 && end > start)
                ranges.Add(new AesRange(start, end));
        }
        return ranges.Count == 0 ? (null, []) : (key, ranges);
    }

    private static int ReadInt(ReadOnlySpan<byte> plain, long offset)
        => BinaryPrimitives.ReadInt32LittleEndian(Slice(plain, offset, 4));

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> plain, long offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > plain.Length)
            throw new Bhd5Exception($"The BHD5 index is cut short or points outside itself: {length} bytes at {offset} of {plain.Length}");
        return plain.Slice((int)offset, length);
    }
}
