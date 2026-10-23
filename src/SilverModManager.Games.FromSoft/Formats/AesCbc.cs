using System.Security.Cryptography;

namespace SilverModManager.Games.FromSoft.Formats;

/// <summary>
/// AES without padding, the way FromSoft uses it. The caller owns padding: a regulation appends its own block, and
/// the archive reader slices on padded sizes. Sizes are checked here so the BCL never sees a short key.
/// </summary>
public static class AesCbc
{
    public const int BlockSize = 16;

    private const int CbcKeySize = 32;
    private const int EcbKeySize = 16;

    public static byte[] EncryptCbc(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> plaintext)
    {
        CheckCbc(key, iv, plaintext);
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes.EncryptCbc(plaintext, iv, PaddingMode.None);
    }

    public static byte[] DecryptCbc(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> ciphertext)
    {
        CheckCbc(key, iv, ciphertext);
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes.DecryptCbc(ciphertext, iv, PaddingMode.None);
    }

    /// <summary>
    /// ECB with a 128-bit key. Exists for the per-file payloads of the packed game archives, whose key sits in the
    /// archive header. ECB encrypts equal blocks to equal ciphertext; use it for nothing else.
    /// </summary>
    public static byte[] DecryptEcb(ReadOnlySpan<byte> key, ReadOnlySpan<byte> ciphertext)
    {
        if (key.Length != EcbKeySize)
            throw new AesException($"AES-128-ECB needs a key of {EcbKeySize} bytes, got {key.Length}");
        if (ciphertext.Length % BlockSize != 0)
            throw new AesException($"ECB works in whole {BlockSize}-byte blocks; got {ciphertext.Length} bytes, which is not a multiple of {BlockSize}. A .bdt slice was cut to the unpadded size instead of the padded one");
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes.DecryptEcb(ciphertext, PaddingMode.None);
    }

    private static void CheckCbc(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> data)
    {
        if (key.Length != CbcKeySize)
            throw new AesException($"AES-256 needs a key of {CbcKeySize} bytes, got {key.Length}");
        if (iv.Length != BlockSize)
            throw new AesException($"CBC needs an IV of {BlockSize} bytes, got {iv.Length}");
        if (data.Length % BlockSize != 0)
            throw new AesException($"CBC works in whole {BlockSize}-byte blocks; got {data.Length} bytes, which is not a multiple of {BlockSize}. The caller sliced the buffer wrong");
    }
}
