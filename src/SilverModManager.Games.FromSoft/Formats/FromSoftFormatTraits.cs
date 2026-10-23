using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;

namespace NexusMods.Games.FromSoft.Formats;

/// <summary>How a game's binder archives (BND3 or BND4) are laid out.</summary>
public enum BinderFormat
{
    Bnd3,
    Bnd4,
}

/// <summary>The compression wrapper (DCX) around a file, named by its four-character magic.</summary>
public enum DcxCompression
{
    /// <summary>The file is not wrapped.</summary>
    None,

    /// <summary>zlib (<c>DFLT</c>).</summary>
    Dflt,

    /// <summary>Oodle Kraken (<c>KRAK</c>). Readable only; nothing here writes it.</summary>
    Kraken,

    /// <summary>Zstandard (<c>ZSTD</c>).</summary>
    Zstd,
}

/// <summary>How a file is wrapped when it is written back out.</summary>
/// <param name="Compression">The wrapper. Only <see cref="DcxCompression.Dflt"/> and <see cref="DcxCompression.Zstd"/> can be written.</param>
/// <param name="Unk04">The word at offset 4 of the DCX header, which differs between the game generations.</param>
/// <param name="Level">The compression level, also recorded in the header; zlib takes 1 to 9.</param>
/// <param name="WindowLog">The Zstandard window size, as a power of two, that caps what the game's decoder must allocate; 0 when it is not Zstandard.</param>
public sealed record DcxWriteFormat(DcxCompression Compression, uint Unk04, int Level, int WindowLog);

/// <summary>How the entries of a game's archive index (BHD5) are laid out.</summary>
public enum Bhd5Layout
{
    /// <summary>A salt after the header, then 40-byte entries: path hash (8), padded size, unpadded size, offset, a digest offset, an AES key offset.</summary>
    Salted40ByteEntries,
}

/// <summary>One packed archive: an RSA-signed index (<c>Name.bhd</c>) beside its payloads (<c>Name.bdt</c>).</summary>
/// <param name="Name">The file name without extension.</param>
/// <param name="Modulus">The public modulus the game decrypts the index with.</param>
/// <param name="Exponent">
/// The public exponent. Not an int: one shipped key uses 0xffffffff, which does not fit a signed 32-bit value.
/// </param>
public sealed record ArchiveIndex(string Name, BigInteger Modulus, BigInteger Exponent)
{
    /// <summary>An archive whose modulus is written as big-endian hex, the way the keys are published.</summary>
    public static ArchiveIndex FromHex(string name, string modulusHex, ulong exponent)
        => new(name, BigInteger.Parse("0" + modulusHex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture), exponent);
}

/// <summary>
/// The file format parameters that differ from one FromSoft game to the next: the values a binder, DCX or
/// regulation reader would otherwise hard-code for one game.
/// </summary>
/// <param name="Binder">The binder format of the game's archives.</param>
/// <param name="ReadableCompressions">The wrappers found in the game's files; a reader must accept each.</param>
/// <param name="PackedWrite">The wrapper a rewritten archive gets.</param>
/// <param name="RegulationFileName">The file in the game folder that holds the param tables; null when the game has none.</param>
/// <param name="RegulationWrite">The wrapper inside the regulation's encryption; null when the game has no regulation.</param>
/// <param name="RegulationKey">The AES-256 key of the regulation. It ships with the game; empty when the regulation is not encrypted.</param>
/// <param name="ParamdefSet">
/// Names the family of param layouts the game uses. The param layout catalogue will be keyed by this and the build
/// from <see cref="Build.BuildIdentity"/>; the catalogue itself does not exist yet.
/// </param>
/// <param name="Archives">The game's packed archives in the order a lookup walks them; empty when the game's files are not read from archives.</param>
/// <param name="PathHashBits">The width, 1 to 64, the archive index hashes paths to.</param>
/// <param name="PathHashPrime">The multiplier of the path hash; meaningless when <paramref name="Archives"/> is empty.</param>
/// <param name="ArchiveLayout">How the index of each archive lays out its entries.</param>
public sealed record FromSoftFormatTraits(
    BinderFormat Binder,
    ImmutableArray<DcxCompression> ReadableCompressions,
    DcxWriteFormat PackedWrite,
    string? RegulationFileName,
    DcxWriteFormat? RegulationWrite,
    ImmutableArray<byte> RegulationKey,
    string ParamdefSet,
    ImmutableArray<ArchiveIndex> Archives,
    int PathHashBits,
    int PathHashPrime,
    Bhd5Layout ArchiveLayout);
