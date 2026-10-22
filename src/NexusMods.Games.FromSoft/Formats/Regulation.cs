using System.Security.Cryptography;
using System.Text;

namespace NexusMods.Games.FromSoft.Formats;

/// <summary>
/// The regulation file: every param table of the game, in three layers. Outermost is AES-256-CBC with the IV in the
/// first 16 bytes; inside it the game's DCX; inside that a binder with one member per param table.
/// </summary>
/// <remarks>
/// Padding was measured from shipped files rather than assumed. FromSoft's own encryptor zero-pads the DCX to a block
/// and stops, while SoulsFormats-derived tools zero-pad and then append a full PKCS#7 block. <see cref="Pack"/> emits
/// the latter, because that is what mod-authored regulations ship and the game loads them.
/// </remarks>
public static class Regulation
{
    public const int IvSize = AesCbc.BlockSize;

    private const int PadBlock = AesCbc.BlockSize;
    private const int VersionOffset = 0x18;
    private const int VersionLength = 8;

    /// <summary>The binder inside <paramref name="blob"/>, decrypted and decompressed.</summary>
    public static byte[] Unpack(ReadOnlySpan<byte> blob, FromSoftFormatTraits traits)
    {
        RequireRegulation(traits);
        if (blob.Length < IvSize)
            throw new RegulationException($"A regulation is at least {IvSize} bytes (its IV); got {blob.Length}");
        var dcx = AesCbc.DecryptCbc(traits.RegulationKey.AsSpan(), blob[..IvSize], blob[IvSize..]);
        return Dcx.Read(dcx, traits);
    }

    /// <summary>The binder members inside <paramref name="blob"/>.</summary>
    public static IReadOnlyList<BinderEntry> Entries(ReadOnlySpan<byte> blob, FromSoftFormatTraits traits)
        => Binders.For(traits).Read(Unpack(blob, traits));

    /// <summary>
    /// Compresses, pads and encrypts <paramref name="payload"/> into a regulation file. The IV comes from the file
    /// being replaced rather than being generated, so the output differs from its input only where the content does.
    /// </summary>
    public static byte[] Pack(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> iv, FromSoftFormatTraits traits)
    {
        var write = RequireRegulation(traits);
        if (iv.Length != IvSize)
            throw new AesException($"CBC needs an IV of {IvSize} bytes, got {iv.Length}");
        var body = Dcx.Write(payload, write);
        var padded = new byte[body.Length + (-body.Length & (PadBlock - 1)) + PadBlock];
        body.CopyTo(padded, 0);
        padded.AsSpan(^PadBlock..).Fill(PadBlock);
        var cipher = AesCbc.EncryptCbc(traits.RegulationKey.AsSpan(), iv, padded);
        return [..iv, ..cipher];
    }

    /// <summary>The build stamp in an already-decrypted regulation, so a caller that unpacked for another reason pays no second AES pass.</summary>
    public static string Version(ReadOnlySpan<byte> payload)
    {
        // A wrong key or a partial decrypt still yields bytes; without these checks the stamp would be whatever sat there.
        if (!payload.StartsWith("BND4"u8))
            throw new RegulationException($"The regulation did not decrypt to a BND4 archive (starts {Convert.ToHexString(payload[..Math.Min(4, payload.Length)])})");
        if (payload.Length < VersionOffset + VersionLength)
            throw new RegulationException($"The regulation payload is too short to hold a build stamp ({payload.Length} bytes)");
        var stamp = payload.Slice(VersionOffset, VersionLength);
        // Encoding.ASCII would turn a stray byte into '?' and hand back a stamp that looks valid.
        if (!Ascii.IsValid(stamp))
            throw new RegulationException($"The regulation's build stamp is not ASCII ({Convert.ToHexString(stamp)})");
        return Encoding.ASCII.GetString(stamp);
    }

    /// <summary>The SHA-256 of the whole file, lowercase hex.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> blob) => Convert.ToHexStringLower(SHA256.HashData(blob));

    private static DcxWriteFormat RequireRegulation(FromSoftFormatTraits traits)
    {
        if (traits.RegulationFileName is not null && traits.RegulationWrite is { } write && !traits.RegulationKey.IsDefaultOrEmpty)
            return write;
        throw new RegulationException($"The {traits.ParamdefSet} game has no regulation file, or none that is encrypted, so there is nothing to unpack or pack");
    }
}
