using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NexusMods.Games.FromSoft.Formats;

namespace NexusMods.Games.FromSoft.Tests.Formats.Fixtures;

/// <summary>
/// Builds archive indexes and the packed files around them: the plaintext BHD5, the RSA wrapping FromSoft applies with
/// the private exponent, and ECB-encrypted payloads.
/// </summary>
public static class Bhd5Fixture
{
    public const int RsaBlock = 256;

    private const int PlainBlock = RsaBlock - 1;
    private const int EntrySize = 40;

    public sealed record Entry(
        ulong NameHash,
        int PaddedSize,
        int UnpaddedSize,
        long Offset,
        byte[]? AesKey = null,
        IReadOnlyList<(long Start, long End)>? Ranges = null);

    /// <summary>A made-up key pair, so a synthetic index can be signed the way FromSoft signs the real ones.</summary>
    public sealed record SigningKey(BigInteger Modulus, BigInteger PublicExponent, BigInteger PrivateExponent)
    {
        public ArchiveIndex Archive(string name) => new(name, Modulus, PublicExponent);
    }

    public static readonly SigningKey TestKey = GenerateKey();

    private static SigningKey GenerateKey()
    {
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(includePrivateParameters: true);
        return new SigningKey(Unsigned(p.Modulus!), Unsigned(p.Exponent!), Unsigned(p.D!));
    }

    private static BigInteger Unsigned(byte[] bigEndian) => new(bigEndian, isUnsigned: true, isBigEndian: true);

    /// <summary>
    /// A plaintext index the parser should accept: the fixed header, the bucket directory, then the entry records,
    /// with the key blocks of encrypted entries after them. Entries go to bucket <c>hash % bucketCount</c>.
    /// </summary>
    public static byte[] Build(IReadOnlyList<(ulong NameHash, int PaddedSize, int UnpaddedSize, long Offset)> entries, int bucketCount = 4, string salt = "GR_test")
        => Build(entries.Select(e => new Entry(e.NameHash, e.PaddedSize, e.UnpaddedSize, e.Offset)).ToArray(), bucketCount, salt);

    public static byte[] Build(IReadOnlyList<Entry> entries, int bucketCount = 4, string salt = "GR_test")
    {
        var saltBytes = Encoding.ASCII.GetBytes(salt);
        var headLength = 28 + saltBytes.Length;
        var entryArea = headLength + bucketCount * 8;
        var buckets = Enumerable.Range(0, bucketCount).Select(_ => new List<Entry>()).ToArray();
        foreach (var e in entries)
            buckets[(int)(e.NameHash % (ulong)bucketCount)].Add(e);

        var keyArea = entryArea + entries.Count * EntrySize;
        var directory = new MemoryStream();
        var records = new MemoryStream();
        var keys = new MemoryStream();
        var next = entryArea;
        foreach (var bucket in buckets)
        {
            WriteInt(directory, bucket.Count);
            WriteInt(directory, next);
            foreach (var e in bucket)
            {
                var keyOffset = 0L;
                if (e.AesKey is not null)
                {
                    keyOffset = keyArea + keys.Length;
                    keys.Write(e.AesKey);
                    WriteInt(keys, e.Ranges!.Count);
                    foreach (var (start, end) in e.Ranges)
                    {
                        WriteLong(keys, start);
                        WriteLong(keys, end);
                    }
                }
                WriteLong(records, (long)e.NameHash);
                WriteInt(records, e.PaddedSize);
                WriteInt(records, e.UnpaddedSize);
                WriteLong(records, e.Offset);
                WriteLong(records, 0);
                WriteLong(records, keyOffset);
                next += EntrySize;
            }
        }

        var blob = new MemoryStream();
        blob.Write("BHD5"u8);
        blob.Write([0xff, 1, 0, 0]);
        WriteInt(blob, 1);
        WriteInt(blob, (int)(headLength + directory.Length + records.Length + keys.Length));
        WriteInt(blob, bucketCount);
        WriteInt(blob, headLength);
        WriteInt(blob, saltBytes.Length);
        blob.Write(saltBytes);
        directory.WriteTo(blob);
        records.WriteTo(blob);
        keys.WriteTo(blob);
        return blob.ToArray();
    }

    /// <summary>
    /// Wraps a plaintext index the way FromSoft does: 255-byte blocks, each raised to the private exponent and written
    /// as 256 bytes. The last block is zero-padded, which the parser never reads.
    /// </summary>
    public static byte[] Sign(ReadOnlySpan<byte> plain, SigningKey key)
    {
        var blocks = (plain.Length + PlainBlock - 1) / PlainBlock;
        var signed = new byte[blocks * RsaBlock];
        for (var i = 0; i < blocks; i++)
        {
            var chunk = new byte[PlainBlock];
            plain[(i * PlainBlock)..Math.Min(plain.Length, (i + 1) * PlainBlock)].CopyTo(chunk);
            var cipher = BigInteger.ModPow(Unsigned(chunk), key.PrivateExponent, key.Modulus);
            if (!cipher.TryWriteBytes(signed.AsSpan(i * RsaBlock, RsaBlock), out var written, isUnsigned: true, isBigEndian: true))
                throw new InvalidOperationException("A signed block does not fit its 256 bytes");
            // The big-endian write is minimal; the block is left-padded to the modulus width.
            signed.AsSpan(i * RsaBlock, written).CopyTo(signed.AsSpan(i * RsaBlock + RsaBlock - written, written));
            signed.AsSpan(i * RsaBlock, RsaBlock - written).Clear();
        }
        return signed;
    }

    /// <summary>AES-128-ECB over the whole of <paramref name="data"/>, zero-padded to a block, as a packed payload is stored.</summary>
    public static byte[] EncryptPayload(ReadOnlySpan<byte> data, byte[] key)
    {
        var padded = new byte[(data.Length + 15) & ~15];
        data.CopyTo(padded);
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptEcb(padded, PaddingMode.None);
    }

    private static void WriteInt(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        s.Write(b);
    }

    private static void WriteLong(Stream s, long value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, value);
        s.Write(b);
    }
}
